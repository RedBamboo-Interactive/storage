// Storage actions as descriptors for the Leaf context menu (right-click and
// long-press). Only a type import, so node tests load this module directly.
// Every item calls a handler that already backs a button; destructive items go
// through the existing confirmation (the quarantine preview, the typed purge ID).
import type { ContextMenuEntry } from "@redbamboo/ui"

/** The parts of a storage node the tile menu needs. */
export interface StorageTileNode {
  logicalPath: string
  kind: "directory" | "file" | "aggregate"
  cleanupKind: "quarantine" | "protected"
  browsable: boolean
  childCount: number
}

/**
 * The absolute local path a node stands for, or null. Aggregates ("Other
 * items") are summaries, and a path under an environment variable
 * (`%LOCALAPPDATA%\RedLeaf`) cannot be resolved in the browser.
 */
export function localPathOf(node: Pick<StorageTileNode, "logicalPath" | "kind">): string | null {
  if (node.kind === "aggregate") return null
  const path = node.logicalPath.trim()
  if (path.includes("%")) return null
  return /^[A-Za-z]:[\\/]/.test(path) || /^\\\\[^\\]/.test(path) ? path : null
}

/** The `file` context-menu subject for a node (Code's "Open in Code"), or null. */
export function fileSubjectOf(node: Pick<StorageTileNode, "logicalPath" | "kind">): { path: string; directory?: true } | null {
  const path = localPathOf(node)
  if (!path) return null
  return node.kind === "directory" ? { path, directory: true } : { path }
}

export interface StorageTileActions {
  node: StorageTileNode
  /** A scan is running (the Analyze and quarantine buttons wait for it). */
  scanning: boolean
  /** A cleanup preview, quarantine or restore is in flight. */
  working: boolean
  analyzing: boolean
  browsing: boolean
  /** Opens the node, as a second click, double-click or Enter does. */
  onOpen: () => void
  /** Selects the node and runs the advisory analysis. */
  onAnalyze: () => void
  /** Selects the node and requests the cleanup preview (its confirmation dialog). */
  onQuarantine: () => void
}

export function storageTileEntries({ node, scanning, working, analyzing, browsing, onOpen, onAnalyze, onQuarantine }: StorageTileActions): ContextMenuEntry[] {
  const entries: ContextMenuEntry[] = []
  if (node.browsable || node.childCount > 0) {
    entries.push({ id: "open", label: "Open", icon: "ph-bold ph-magnifying-glass-plus", disabled: browsing, run: () => onOpen() })
  }
  entries.push({
    id: "analyze",
    label: "Analyze",
    icon: "ph-bold ph-sparkle",
    disabled: analyzing || (scanning && !node.browsable),
    run: () => onAnalyze(),
  })
  if (node.kind !== "aggregate") {
    entries.push({ id: "copy-path", label: "Copy path", icon: "ph-bold ph-copy", run: (ctx) => { void ctx.copyText(node.logicalPath) } })
  }
  if (node.cleanupKind === "quarantine") {
    entries.push(
      { id: "quarantine-separator", separator: true },
      { id: "quarantine", label: "Move to quarantine…", icon: "ph-bold ph-archive", danger: true, disabled: working || scanning, run: () => onQuarantine() },
    )
  }
  return entries
}

export interface QuarantineRowActions {
  working: boolean
  onRestore: () => void
  /** Opens the existing typed-ID purge confirmation under the row. */
  onPurge: () => void
}

export function quarantineRowEntries({ working, onRestore, onPurge }: QuarantineRowActions): ContextMenuEntry[] {
  return [
    { id: "restore", label: "Restore", icon: "ph-bold ph-arrow-counter-clockwise", disabled: working, run: () => onRestore() },
    { id: "purge-separator", separator: true },
    { id: "purge", label: "Purge…", icon: "ph-bold ph-trash", danger: true, disabled: working, run: () => onPurge() },
  ]
}
