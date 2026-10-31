import assert from "node:assert/strict"
import test from "node:test"
import { fileSubjectOf, localPathOf, quarantineRowEntries, storageTileEntries, type StorageTileActions } from "./storage-actions.ts"

type Item = { id: string; label?: string; danger?: boolean; disabled?: boolean; separator?: boolean; run?: (ctx: unknown) => void }
const ids = (entries: unknown[]) => (entries as Item[]).map(entry => entry.id)
const item = (entries: unknown[], id: string) => (entries as Item[]).find(entry => entry.id === id)!

function tile(overrides: Partial<StorageTileActions> = {}, node: Partial<StorageTileActions["node"]> = {}) {
  const calls: string[] = []
  const actions: StorageTileActions = {
    node: { logicalPath: "S:\\RedCompute\\scratch", kind: "directory", cleanupKind: "quarantine", browsable: true, childCount: 0, ...node },
    scanning: false,
    working: false,
    analyzing: false,
    browsing: false,
    onOpen: () => calls.push("open"),
    onAnalyze: () => calls.push("analyze"),
    onQuarantine: () => calls.push("quarantine"),
    ...overrides,
  }
  return { entries: storageTileEntries(actions), calls }
}

test("disposable folder tile offers open, analyze, copy path and the quarantine request", () => {
  const { entries, calls } = tile()
  assert.deepEqual(ids(entries), ["open", "analyze", "copy-path", "quarantine-separator", "quarantine"])
  const quarantine = item(entries, "quarantine")
  assert.equal(quarantine.danger, true)
  assert.match(quarantine.label!, /…$/)
  for (const id of ["open", "analyze", "quarantine"]) item(entries, id).run!({})
  assert.deepEqual(calls, ["open", "analyze", "quarantine"])
})

test("protected file tile has no open or quarantine item", () => {
  const { entries } = tile({}, { kind: "file", cleanupKind: "protected", browsable: false })
  assert.deepEqual(ids(entries), ["analyze", "copy-path"])
})

test("a folder with scanned children opens even when not browsable", () => {
  const { entries } = tile({}, { browsable: false, childCount: 3, cleanupKind: "protected" })
  assert.deepEqual(ids(entries), ["open", "analyze", "copy-path"])
})

test("aggregates cannot copy a path", () => {
  const { entries } = tile({}, { kind: "aggregate", cleanupKind: "protected", logicalPath: "S:\\Other items" })
  assert.deepEqual(ids(entries), ["open", "analyze"])
})

test("disabled states mirror the buttons", () => {
  const scanning = tile({ scanning: true }, { browsable: false })
  assert.equal(item(scanning.entries, "analyze").disabled, true)
  assert.equal(item(scanning.entries, "quarantine").disabled, true)
  const scanningBrowsable = tile({ scanning: true })
  assert.equal(item(scanningBrowsable.entries, "analyze").disabled, false)
  assert.equal(item(tile({ analyzing: true }).entries, "analyze").disabled, true)
  assert.equal(item(tile({ working: true }).entries, "quarantine").disabled, true)
  assert.equal(item(tile({ browsing: true }).entries, "open").disabled, true)
})

test("copy path copies the logical path shown in the detail panel", async () => {
  const { entries } = tile()
  const copied: string[] = []
  item(entries, "copy-path").run!({ copyText: async (text: string) => { copied.push(text) } })
  assert.deepEqual(copied, ["S:\\RedCompute\\scratch"])
})

test("only absolute local paths become file subjects", () => {
  assert.equal(localPathOf({ logicalPath: "S:\\", kind: "directory" }), "S:\\")
  assert.equal(localPathOf({ logicalPath: "L:/Workspaces/Nova", kind: "directory" }), "L:/Workspaces/Nova")
  assert.equal(localPathOf({ logicalPath: "\\\\server\\share\\x", kind: "file" }), "\\\\server\\share\\x")
  assert.equal(localPathOf({ logicalPath: "%LOCALAPPDATA%\\RedLeaf\\logs", kind: "directory" }), null)
  assert.equal(localPathOf({ logicalPath: "S:\\Other items", kind: "aggregate" }), null)
  assert.equal(localPathOf({ logicalPath: "Agent workspaces", kind: "directory" }), null)
})

test("folders are directory file subjects, files are plain", () => {
  assert.deepEqual(fileSubjectOf({ logicalPath: "S:\\a", kind: "directory" }), { path: "S:\\a", directory: true })
  assert.deepEqual(fileSubjectOf({ logicalPath: "S:\\a.txt", kind: "file" }), { path: "S:\\a.txt" })
  assert.equal(fileSubjectOf({ logicalPath: "%LOCALAPPDATA%\\RedLeaf", kind: "directory" }), null)
})

test("quarantine row restores directly and purges through the typed confirmation", () => {
  const calls: string[] = []
  const entries = quarantineRowEntries({ working: false, onRestore: () => calls.push("restore"), onPurge: () => calls.push("purge") })
  assert.deepEqual(ids(entries), ["restore", "purge-separator", "purge"])
  assert.equal(item(entries, "purge").danger, true)
  assert.match(item(entries, "purge").label!, /…$/)
  item(entries, "restore").run!({})
  item(entries, "purge").run!({})
  assert.deepEqual(calls, ["restore", "purge"])
  const busy = quarantineRowEntries({ working: true, onRestore: () => {}, onPurge: () => {} })
  assert.equal(item(busy, "restore").disabled, true)
  assert.equal(item(busy, "purge").disabled, true)
})
