import { useCallback, useEffect, useMemo, useRef, useState } from "react"
import { PanelHeader, ScrollArea } from "@redbamboo/ui"
import { api } from "./api"
import { layoutTreemap } from "./treemap"
import type {
  CleanupPreview,
  QuarantineItem,
  StorageAnalysis,
  StorageArea,
  StorageBrowsePage,
  StorageNode,
  StorageStatus,
} from "./types"

const STATUS = "/api/apps/storage/status"

export function StorageApp() {
  const [status, setStatus] = useState<StorageStatus | null>(null)
  const [areaId, setAreaId] = useState<string | null>(null)
  const [navigation, setNavigation] = useState<StorageNode[]>([])
  const [selected, setSelected] = useState<StorageNode | null>(null)
  const [pageHistory, setPageHistory] = useState<(StorageBrowsePage | null)[]>([])
  const [browsing, setBrowsing] = useState(false)
  const [analysis, setAnalysis] = useState<StorageAnalysis | null>(null)
  const [analyzing, setAnalyzing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [working, setWorking] = useState(false)
  const [preview, setPreview] = useState<CleanupPreview | null>(null)
  const [trashOpen, setTrashOpen] = useState(false)
  const [trash, setTrash] = useState<QuarantineItem[]>([])
  const [purgeTarget, setPurgeTarget] = useState<string | null>(null)
  const [purgeText, setPurgeText] = useState("")
  const autoStarted = useRef(false)
  const browseRequest = useRef<string | null>(null)

  const loadStatus = useCallback(async () => {
    try {
      const next = await api.get<StorageStatus>(STATUS)
      setStatus(next)
      setError(next.progress.error || null)
    } catch (reason) {
      setError(message(reason))
    }
  }, [])

  const startScan = useCallback(async () => {
    setWorking(true)
    setError(null)
    try {
      await api.post("/api/apps/storage/scan")
      await loadStatus()
    } catch (reason) {
      setError(message(reason))
    } finally {
      setWorking(false)
    }
  }, [loadStatus])

  useEffect(() => { void loadStatus() }, [loadStatus])
  useEffect(() => {
    if (!status || status.progress.running || status.snapshot || autoStarted.current) return
    autoStarted.current = true
    void startScan()
  }, [status, startScan])
  useEffect(() => {
    if (!status?.progress.running) return
    const timer = window.setInterval(() => { void loadStatus() }, 1000)
    return () => window.clearInterval(timer)
  }, [status?.progress.running, loadStatus])

  const snapshot = status?.snapshot
  const area = useMemo(() => {
    if (!snapshot) return null
    return snapshot.areas.find(item => item.id === areaId) ?? snapshot.areas[0] ?? null
  }, [snapshot, areaId])
  const focus = navigation[navigation.length - 1] ?? area?.root ?? null
  const breadcrumbs = navigation.length > 0 ? navigation : area ? [area.root] : []
  const browsePage = pageHistory[pageHistory.length - 1] ?? null
  const tiles = useMemo(() => layoutTreemap(focus?.children ?? []), [focus])
  const parent = breadcrumbs.length > 1 ? breadcrumbs[breadcrumbs.length - 2] : null

  useEffect(() => {
    if (!snapshot?.areas.length) return
    const nextArea = snapshot.areas.find(item => item.id === areaId) ?? snapshot.areas[0]
    setAreaId(nextArea.id)
    setNavigation([nextArea.root])
    setSelected(nextArea.root)
    setPageHistory([null])
  }, [snapshot?.id])

  useEffect(() => {
    if (!area || navigation.length !== 1 || navigation[0]?.id !== area.root.id) return
    setNavigation([area.root])
    if (selected?.id === area.root.id) setSelected(area.root)
  }, [area?.root])

  useEffect(() => { setAnalysis(null) }, [selected?.id])

  const chooseArea = (next: StorageArea) => {
    setAreaId(next.id)
    setNavigation([next.root])
    setSelected(next.root)
    setPageHistory([null])
  }

  const openNode = async (node: StorageNode) => {
    setSelected(node)
    if (!snapshot) return
    if (node.browsable) {
      if (browseRequest.current === node.id) return
      browseRequest.current = node.id
      setBrowsing(true)
      setError(null)
      try {
        const page = await api.post<StorageBrowsePage>("/api/apps/storage/browse", {
          nodeId: node.id,
          snapshotId: snapshot.id,
          offset: 0,
        })
        setNavigation(items => [...items, page.node])
        setPageHistory(items => [...items, page])
        setSelected(page.node)
      } catch (reason) { setError(message(reason)) }
      finally { browseRequest.current = null; setBrowsing(false) }
      return
    }
    if (node.children.length > 0) {
      setNavigation(items => [...items, node])
      setPageHistory(items => [...items, null])
    }
  }

  const goHome = () => {
    if (!area) return
    setNavigation([area.root])
    setSelected(area.root)
    setPageHistory([null])
  }

  const goUp = () => {
    if (!parent) return
    setNavigation(items => items.slice(0, -1))
    setPageHistory(items => items.slice(0, -1))
    setSelected(parent)
  }

  const jumpTo = (index: number) => {
    const node = breadcrumbs[index]
    setNavigation(items => items.slice(0, index + 1))
    setPageHistory(items => items.slice(0, index + 1))
    setSelected(node)
  }

  const changePage = async (offset: number) => {
    if (!focus || !snapshot) return
    setBrowsing(true)
    setError(null)
    try {
      const page = await api.post<StorageBrowsePage>("/api/apps/storage/browse", {
        nodeId: focus.id, snapshotId: snapshot.id, offset,
      })
      setNavigation(items => [...items.slice(0, -1), page.node])
      setPageHistory(items => [...items.slice(0, -1), page])
      setSelected(page.node)
    } catch (reason) { setError(message(reason)) }
    finally { setBrowsing(false) }
  }

  const analyzeSelected = async () => {
    if (!selected || !snapshot) return
    setAnalyzing(true)
    setAnalysis(null)
    setError(null)
    try {
      setAnalysis(await api.post<StorageAnalysis>("/api/apps/storage/analyze", {
        nodeId: selected.id, snapshotId: snapshot.id,
      }))
    } catch (reason) { setError(message(reason)) }
    finally { setAnalyzing(false) }
  }

  const requestCleanup = async () => {
    if (!selected || !snapshot) return
    setWorking(true)
    setError(null)
    try {
      setPreview(await api.post<CleanupPreview>("/api/apps/storage/cleanup/preview", {
        nodeId: selected.id,
        snapshotId: snapshot.id,
      }))
    } catch (reason) {
      setError(message(reason))
    } finally {
      setWorking(false)
    }
  }

  const confirmCleanup = async () => {
    if (!preview) return
    setWorking(true)
    try {
      await api.post("/api/apps/storage/cleanup/quarantine", { token: preview.token })
      setPreview(null)
      await startScan()
    } catch (reason) {
      setError(message(reason))
    } finally {
      setWorking(false)
    }
  }

  const openTrash = async () => {
    setTrashOpen(true)
    try {
      const response = await api.get<{ items: QuarantineItem[] }>("/api/apps/storage/quarantine")
      setTrash(response.items)
    } catch (reason) { setError(message(reason)) }
  }

  const restore = async (id: string) => {
    setWorking(true)
    try {
      await api.post(`/api/apps/storage/quarantine/${id}/restore`)
      setTrash(items => items.filter(item => item.id !== id))
      await startScan()
    } catch (reason) { setError(message(reason)) }
    finally { setWorking(false) }
  }

  const purge = async (id: string) => {
    if (purgeText !== id) return
    setWorking(true)
    try {
      await api.post(`/api/apps/storage/quarantine/${id}/purge`, { confirmation: purgeText })
      setTrash(items => items.filter(item => item.id !== id))
      setPurgeTarget(null)
      setPurgeText("")
    } catch (reason) { setError(message(reason)) }
    finally { setWorking(false) }
  }

  return (
    <div className="storage-root h-full min-h-0 flex flex-col text-contrast">
      <PanelHeader
        leading={<i className="ph-bold ph-hard-drives text-primary" />}
        title="Storage"
      >
        <div className="flex items-center gap-2">
          <button className="storage-button storage-button-muted" onClick={() => void openTrash()}>
            <i className="ph-bold ph-archive" /> Quarantine
          </button>
          <button className="storage-button storage-button-primary" disabled={working || status?.progress.running}
            onClick={() => void startScan()}>
            <i className={status?.progress.running ? "ph-bold ph-spinner-gap animate-spin" : "ph-bold ph-scan"} />
            {status?.progress.running ? "Scanning" : "Scan now"}
          </button>
        </div>
      </PanelHeader>

      {status?.progress.running && (
        <div className="px-4 py-2 border-b border-overlay-6 bg-primary/5 flex items-center gap-3 text-xs">
          <i className="ph-bold ph-spinner-gap animate-spin text-primary" />
          <span className="font-medium">{status.progress.currentArea || "Preparing"}</span>
          <span className="text-text-muted">{formatBytes(status.progress.bytesVisited)} visited</span>
          <span className="text-text-disabled tabular-nums">{status.progress.filesVisited.toLocaleString()} files</span>
        </div>
      )}
      {error && (
        <div className="px-4 py-2 border-b border-red-500/30 bg-red-500/10 text-red-300 text-xs flex gap-2">
          <i className="ph-bold ph-warning" /><span>{error}</span>
        </div>
      )}

      {!snapshot ? (
        <div className="flex-1 flex items-center justify-center">
          <div className="text-center max-w-md px-8">
            <i className="ph-thin ph-hard-drives text-5xl text-primary" />
            <h2 className="text-xl font-semibold mt-4">Reading Leaf's footprint</h2>
            <p className="text-sm text-text-muted mt-2">
              Scratch can contain millions of loose items, so the first scan may take a moment.
              The scanner never follows links and keeps only the largest branches in memory.
            </p>
          </div>
        </div>
      ) : (
        <>
          <div className="storage-metrics px-4 py-3 border-b border-overlay-6 grid gap-2">
            <Metric icon="ph-bold ph-disc" label="Physical Leaf data" value={formatBytes(snapshot.physicalBytes)} />
            <Metric icon="ph-bold ph-folders" label="Logical workspaces" value={formatBytes(snapshot.logicalBytes)} />
            <Metric icon="ph-bold ph-files" label="Files scanned"
              value={snapshot.areas.reduce((sum, item) => sum + item.root.fileCount, 0).toLocaleString()} />
            <Metric icon="ph-bold ph-clock" label="Snapshot"
              value={status?.progress.running ? "Live scan" : relativeTime(snapshot.completedAt)} />
          </div>

          <div className="px-4 py-2 border-b border-overlay-6 flex gap-2 overflow-x-auto">
            {snapshot.areas.map(item => (
              <button key={item.id} onClick={() => chooseArea(item)}
                className={`storage-area-chip ${item.id === area?.id ? "storage-area-chip-active" : ""}`}>
                <i className={item.id === "scratch" ? "ph-bold ph-lightning" :
                  item.id === "workspaces" ? "ph-bold ph-users-three" : "ph-bold ph-database"} />
                <span>{item.name}</span>
                <strong>{formatBytes(item.root.sizeBytes)}</strong>
                {item.logicalOnly && <em>logical</em>}
              </button>
            ))}
          </div>

          <div className="storage-main flex-1 min-h-0 grid">
            <section className="storage-map-panel flex flex-col border-r border-overlay-6">
              <div className="storage-navigation border-b border-overlay-6">
                <div className="storage-navigation-actions">
                  <button className="storage-icon-button" aria-label="Storage root" disabled={!parent}
                    onClick={goHome} title="Storage root">
                    <i className="ph-bold ph-house" />
                  </button>
                  <button className="storage-icon-button" aria-label="Up one level" disabled={!parent}
                    onClick={goUp} title="Up one level">
                    <i className="ph-bold ph-arrow-up" />
                  </button>
                  <button className="storage-icon-button" aria-label="Analyze selected item"
                    disabled={!selected || analyzing || status?.progress.running && !selected.browsable}
                    onClick={() => void analyzeSelected()} title="Analyze selected item">
                    <i className={analyzing ? "ph-bold ph-spinner-gap animate-spin" : "ph-bold ph-sparkle"} />
                  </button>
                </div>
                <div className="storage-breadcrumbs">
                  {breadcrumbs.map((node, index) => (
                    <span key={node.id} className="flex items-center gap-1">
                      {index > 0 && <i className="ph-bold ph-caret-right text-[10px] text-text-disabled" />}
                      <button className={index === breadcrumbs.length - 1
                        ? "storage-breadcrumb storage-breadcrumb-current"
                        : "storage-breadcrumb"}
                        onClick={() => jumpTo(index)}>{node.name}</button>
                    </span>
                  ))}
                </div>
                <span className="storage-layer-badge">Layer {Math.max(1, breadcrumbs.length)}</span>
              </div>
              <div className="storage-treemap relative flex-1 min-h-0 m-3 rounded-xl overflow-hidden border border-overlay-8">
                <div key={focus?.id} className="storage-map-stage absolute inset-0">
                  {tiles.length === 0 ? (
                    <div className="absolute inset-0 flex items-center justify-center text-sm text-text-muted">
                      {browsing ? "Reading this layer…" : "This branch has no visible children."}
                    </div>
                  ) : tiles.map(tile => (
                    <button key={tile.node.id}
                      className="storage-tile text-left p-2"
                      data-selected={tile.node.id === selected?.id}
                      data-expandable={tile.node.browsable || tile.node.children.length > 0}
                      style={{
                        left: `${tile.x}%`, top: `${tile.y}%`,
                        width: `${tile.width}%`, height: `${tile.height}%`,
                        background: colorFor(tile.node.origin, tile.node.kind === "aggregate"),
                      }}
                      title={`${tile.node.logicalPath}\n${formatBytes(tile.node.sizeBytes)}`}
                      onClick={() => {
                        if (tile.node.id === selected?.id && (tile.node.browsable || tile.node.children.length > 0))
                          void openNode(tile.node)
                        else setSelected(tile.node)
                      }}
                      onDoubleClick={() => void openNode(tile.node)}
                      onKeyDown={event => {
                        if (event.key === "Enter" && (tile.node.browsable || tile.node.children.length > 0))
                          void openNode(tile.node)
                      }}>
                      {tile.width * tile.height > 55 && (
                        <span className="storage-tile-label block min-w-0">
                          <strong className="flex items-center gap-1 text-xs text-white">
                            <span className="truncate">{tile.node.name}</span>
                            {(tile.node.browsable || tile.node.children.length > 0) &&
                              <i className="ph-bold ph-magnifying-glass-plus shrink-0" />}
                          </strong>
                          <small className="block text-[10px] text-white/75 tabular-nums">{formatBytes(tile.node.sizeBytes)}</small>
                        </span>
                      )}
                    </button>
                  ))}
                </div>
              </div>
              {browsePage && focus?.id === browsePage.node.id && (
                <div className="storage-pagination">
                  <button className="storage-icon-button" aria-label="Previous items"
                    disabled={!browsePage.hasPrevious || browsing}
                    onClick={() => void changePage(Math.max(0, browsePage.offset - browsePage.limit))}>
                    <i className="ph-bold ph-caret-left" />
                  </button>
                  <span>{browsePage.total === 0 ? "No items" :
                    `${browsePage.offset + 1}-${Math.min(browsePage.total, browsePage.offset + browsePage.node.children.length)} of ${browsePage.total}`}</span>
                  <button className="storage-icon-button" aria-label="Next items"
                    disabled={!browsePage.hasNext || browsing}
                    onClick={() => void changePage(browsePage.offset + browsePage.limit)}>
                    <i className="ph-bold ph-caret-right" />
                  </button>
                </div>
              )}
              <div className="px-4 pb-3 flex items-center justify-between text-[11px] text-text-disabled">
                <span>Click to inspect. Click the selected tile again, double-click, or press Enter to open it.</span>
                {area?.logicalOnly && <span>L: is logical usage already backed by RedLeaf's database.</span>}
              </div>
            </section>

            <aside className="min-h-0 flex flex-col bg-overlay-2">
              <ScrollArea className="flex-1">
                {selected && (
                  <div className="p-4 space-y-4">
                    <div>
                      <div className="flex items-start gap-3">
                        <div className="w-10 h-10 rounded-lg bg-overlay-8 flex items-center justify-center text-primary shrink-0">
                          <i className={selected.kind === "file" ? "ph-bold ph-file" :
                            selected.kind === "aggregate" ? "ph-bold ph-stack" : "ph-bold ph-folder"} />
                        </div>
                        <div className="min-w-0">
                          <h2 className="font-semibold truncate">{selected.name}</h2>
                          <p className="text-xs text-text-muted break-all mt-0.5">{selected.logicalPath}</p>
                        </div>
                      </div>
                      <div className="text-3xl font-semibold tabular-nums mt-4">{formatBytes(selected.sizeBytes)}</div>
                    </div>
                    <div className="grid grid-cols-2 gap-2">
                      <SmallMetric label="Files" value={selected.fileCount.toLocaleString()} />
                      <SmallMetric label="Folders" value={selected.directoryCount.toLocaleString()} />
                      <SmallMetric label="Owner" value={selected.origin} />
                      <SmallMetric label="Purpose" value={selected.purpose} />
                    </div>
                    {selected.modifiedAt && (
                      <p className="text-xs text-text-muted">Modified {relativeTime(selected.modifiedAt)}</p>
                    )}
                    {selected.truncated && (
                      <p className="storage-note"><i className="ph-bold ph-stack" />
                        This overview is compact. Open it to load a page of concrete files and folders.</p>
                    )}
                    {(selected.browsable || selected.children.length > 0) && selected.id !== focus?.id && (
                      <button className="storage-button storage-button-muted w-full justify-center"
                        disabled={browsing} onClick={() => void openNode(selected)}>
                        <i className={browsing ? "ph-bold ph-spinner-gap animate-spin" : "ph-bold ph-magnifying-glass-plus"} />
                        Open concrete contents
                      </button>
                    )}
                    {(analysis || analyzing) && (
                      <div className="storage-analysis-card space-y-2" aria-live="polite">
                        <div className="flex items-center justify-between gap-2">
                          <div className="text-sm font-semibold flex items-center gap-1.5">
                            <i className={analyzing ? "ph-bold ph-spinner-gap animate-spin text-primary" : "ph-bold ph-sparkle text-primary"} />
                            AI assessment
                          </div>
                          {analysis && <span className="text-[10px] uppercase tracking-wide text-text-disabled">
                            {analysis.recommendation} · {Math.round(analysis.confidence * 100)}%
                          </span>}
                        </div>
                        {analyzing ? <p className="text-xs text-text-muted">Inspecting bounded metadata…</p> : analysis && <>
                          <p className="text-xs leading-relaxed">{analysis.summary}</p>
                          {analysis.contents.length > 0 && <ul className="text-xs text-text-muted space-y-1">
                            {analysis.contents.map((item, index) => <li key={index}>{item}</li>)}
                          </ul>}
                          <div>
                            <div className="text-[10px] uppercase tracking-wide text-text-disabled">If removed</div>
                            <p className="text-xs text-text-muted mt-0.5">{analysis.deletionImpact}</p>
                          </div>
                          <p className="text-xs">{analysis.reasoning}</p>
                          <p className="text-[10px] text-text-disabled">
                            Advisory only. No files changed.{analysis.model ? ` ${analysis.model}` : ""}
                          </p>
                        </>}
                      </div>
                    )}
                    {selected.cleanupKind === "quarantine" ? (
                      <div className="rounded-xl border border-amber-500/30 bg-amber-500/8 p-3">
                        <div className="text-sm font-medium text-amber-300">Disposable scratch</div>
                        <p className="text-xs text-text-muted mt-1">
                          Removal moves this item into RedLeaf quarantine first. It can be restored until you purge it.
                        </p>
                        <button className="storage-button storage-button-danger w-full justify-center mt-3"
                          disabled={working || status?.progress.running} onClick={() => void requestCleanup()}>
                          <i className="ph-bold ph-archive" /> Move to quarantine
                        </button>
                      </div>
                    ) : (
                      <p className="storage-note"><i className="ph-bold ph-shield-check" />
                        {selected.protectionReason || "This item is protected."}</p>
                    )}
                    <div>
                      <h3 className="text-[10px] uppercase tracking-wider text-text-disabled font-semibold mb-2">
                        Largest children
                      </h3>
                      <div className="space-y-1">
                        {selected.children.slice(0, 12).map(child => (
                          <button key={child.id} className="w-full flex items-center gap-2 px-2 py-1.5 rounded hover:bg-overlay-6 text-xs"
                            onClick={() => void openNode(child)}>
                            <i className={child.kind === "file" ? "ph-bold ph-file" : "ph-bold ph-folder"} />
                            <span className="truncate flex-1 text-left">{child.name}</span>
                            <span className="tabular-nums text-text-muted">{formatBytes(child.sizeBytes)}</span>
                            {(child.browsable || child.children.length > 0) &&
                              <i className="ph-bold ph-caret-right text-text-disabled" />}
                          </button>
                        ))}
                      </div>
                    </div>
                  </div>
                )}
              </ScrollArea>
            </aside>
          </div>
        </>
      )}

      {preview && (
        <div className="storage-modal-backdrop" role="dialog" aria-modal="true">
          <div className="storage-modal">
            <div className="w-12 h-12 rounded-full bg-amber-500/15 text-amber-300 flex items-center justify-center text-xl">
              <i className="ph-bold ph-archive" />
            </div>
            <h2 className="text-lg font-semibold mt-3">Move to quarantine?</h2>
            <p className="text-sm text-text-muted mt-2 break-all">{preview.logicalPath}</p>
            <div className="rounded-lg bg-overlay-5 p-3 mt-3 flex justify-between text-sm">
              <span>{preview.fileCount.toLocaleString()} files</span><strong>{formatBytes(preview.sizeBytes)}</strong>
            </div>
            <p className="text-xs text-text-disabled mt-3">
              Nothing is permanently deleted. Restore remains available from Quarantine.
            </p>
            <div className="flex gap-2 mt-5">
              <button className="storage-button storage-button-muted flex-1 justify-center" onClick={() => setPreview(null)}>Cancel</button>
              <button className="storage-button storage-button-danger flex-1 justify-center" disabled={working}
                onClick={() => void confirmCleanup()}>Quarantine</button>
            </div>
          </div>
        </div>
      )}

      {trashOpen && (
        <div className="storage-modal-backdrop" role="dialog" aria-modal="true">
          <div className="storage-modal storage-modal-wide">
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-lg font-semibold">Quarantine</h2>
                <p className="text-xs text-text-muted mt-1">Restore safely, or explicitly purge to reclaim disk space.</p>
              </div>
              <button className="storage-icon-button" onClick={() => setTrashOpen(false)}><i className="ph-bold ph-x" /></button>
            </div>
            <div className="mt-4 max-h-[55vh] overflow-y-auto space-y-2">
              {trash.length === 0 && <p className="text-sm text-text-muted py-8 text-center">Quarantine is empty.</p>}
              {trash.map(item => (
                <div key={item.id} className="rounded-lg border border-overlay-8 bg-overlay-3 p-3">
                  <div className="flex gap-3 items-start">
                    <i className={item.directory ? "ph-bold ph-folder text-primary mt-0.5" : "ph-bold ph-file text-primary mt-0.5"} />
                    <div className="min-w-0 flex-1">
                      <div className="font-medium text-sm truncate">{item.name}</div>
                      <div className="text-[11px] text-text-muted break-all">{item.originalLogicalPath}</div>
                      <div className="text-xs text-text-disabled mt-1">{formatBytes(item.sizeBytes)} · {relativeTime(item.quarantinedAt)}</div>
                    </div>
                    <button className="storage-button storage-button-muted" disabled={working}
                      onClick={() => void restore(item.id)}>Restore</button>
                    <button className="storage-button storage-button-danger" disabled={working}
                      onClick={() => { setPurgeTarget(item.id); setPurgeText("") }}>Purge</button>
                  </div>
                  {purgeTarget === item.id && (
                    <div className="mt-3 pt-3 border-t border-red-500/20">
                      <p className="text-xs text-red-300">Permanent. Type the quarantine ID to confirm:</p>
                      <code className="text-[10px] text-text-muted select-all">{item.id}</code>
                      <div className="flex gap-2 mt-2">
                        <input className="storage-input flex-1" value={purgeText}
                          onChange={event => setPurgeText(event.target.value)} autoFocus />
                        <button className="storage-button storage-button-danger" disabled={purgeText !== item.id || working}
                          onClick={() => void purge(item.id)}>Delete permanently</button>
                      </div>
                    </div>
                  )}
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function Metric({ icon, label, value }: { icon: string; label: string; value: string }) {
  return <div className="rounded-lg border border-overlay-6 bg-overlay-3 px-3 py-2 flex items-center gap-3">
    <i className={`${icon} text-primary`} />
    <div className="min-w-0"><div className="text-[10px] uppercase tracking-wide text-text-disabled">{label}</div>
      <div className="font-semibold truncate tabular-nums">{value}</div></div>
  </div>
}

function SmallMetric({ label, value }: { label: string; value: string }) {
  return <div className="rounded-lg bg-overlay-4 px-2.5 py-2 min-w-0">
    <div className="text-[10px] uppercase tracking-wide text-text-disabled">{label}</div>
    <div className="text-xs font-medium truncate mt-0.5">{value}</div>
  </div>
}

const colors = ["#b45309", "#c2410c", "#9f1239", "#6d28d9", "#0369a1", "#0f766e", "#3f6212", "#854d0e"]
function colorFor(origin: string, aggregate: boolean) {
  if (aggregate) return "rgb(71 85 105 / .65)"
  let hash = 0
  for (let index = 0; index < origin.length; index++) hash = ((hash << 5) - hash + origin.charCodeAt(index)) | 0
  return colors[Math.abs(hash) % colors.length]
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return "0 B"
  const units = ["B", "KB", "MB", "GB", "TB"]
  const index = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)))
  const amount = value / 1024 ** index
  return `${amount >= 100 || index === 0 ? amount.toFixed(0) : amount.toFixed(1)} ${units[index]}`
}

function relativeTime(value: string) {
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 10) return "just now"
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "Storage operation failed"
}
