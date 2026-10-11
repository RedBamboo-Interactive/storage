import assert from "node:assert/strict"
import { readFile } from "node:fs/promises"
import test from "node:test"
import { layoutTreemap } from "../web/src/treemap.ts"

const root = new URL("../", import.meta.url)

test("manifest exposes the Storage app and guarded filesystem permission", async () => {
  const manifest = JSON.parse(await readFile(new URL("plugin.json", root), "utf8"))
  assert.equal(manifest.id, "storage")
  assert.equal(manifest.frontend.route, "/apps/storage")
  assert.equal(manifest.permissions[0].target, "redleaf-storage")
})

test("frontend keeps cleanup behind preview and quarantine", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  assert.match(app, /cleanup\/preview/)
  assert.match(app, /cleanup\/quarantine/)
  assert.match(app, /Restore remains available/)
  assert.match(app, /Type the quarantine ID/)
})

test("frontend distinguishes logical workspace usage", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  assert.match(app, /Logical workspaces/)
  assert.match(app, /already backed by RedLeaf's database/)
})

test("Storage owns its responsive shell layout", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  const css = await readFile(new URL("web/src/plugin.css", root), "utf8")
  assert.match(app, /storage-root/)
  assert.match(app, /storage-metrics/)
  assert.match(app, /storage-main/)
  assert.match(css, /@media \(min-width: 64rem\)/)
  assert.match(css, /grid-template-columns: repeat\(4/)
  assert.match(css, /grid-template-columns: minmax\(0, 1fr\) 20rem/)
})

test("scan progress drives the live tree and navigation is explicit", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  const css = await readFile(new URL("web/src/plugin.css", root), "utf8")
  assert.match(app, /window\.setInterval\(\(\) => \{ void loadStatus\(\) \}, 1000\)/)
  assert.match(app, /Live scan/)
  assert.match(app, /aria-label="Storage root"/)
  assert.match(app, /aria-label="Up one level"/)
  assert.match(app, /Layer \{Math\.max\(1, breadcrumbs\.length\)\}/)
  assert.match(app, /Click the selected tile again/)
  assert.match(css, /storage-focus-in/)
})

test("aggregates browse concrete pages and selected items have advisory analysis", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  const css = await readFile(new URL("web/src/plugin.css", root), "utf8")
  assert.match(app, /\/api\/apps\/storage\/browse/)
  assert.match(app, /aria-label="Previous items"/)
  assert.match(app, /aria-label="Analyze selected item"/)
  assert.match(app, /\/api\/apps\/storage\/analyze/)
  assert.match(app, /Advisory only\. No files changed/)
  assert.doesNotMatch(css, /max-height:\s*52rem/)
  assert.match(css, /height:\s*100dvh/)
})

test("treemap always terminates for an exact half split", () => {
  const node = (id) => ({ id, sizeBytes: 10 })
  const tiles = layoutTreemap([node("left"), node("right")])
  assert.deepEqual(tiles.map(tile => tile.node.id).sort(), ["left", "right"])
  assert.equal(tiles.reduce((sum, tile) => sum + tile.width * tile.height, 0), 10_000)
})
