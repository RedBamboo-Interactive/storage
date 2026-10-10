import type { StorageNode } from "./types"

export interface Tile {
  node: StorageNode
  x: number
  y: number
  width: number
  height: number
}

interface Bounds { x: number; y: number; width: number; height: number }

export function layoutTreemap(nodes: StorageNode[], bounds: Bounds = { x: 0, y: 0, width: 100, height: 100 }): Tile[] {
  const visible = nodes.filter(node => node.sizeBytes > 0)
    .sort((left, right) => right.sizeBytes - left.sizeBytes)
  const result: Tile[] = []
  split(visible, bounds, result)
  return result
}

function split(nodes: StorageNode[], bounds: Bounds, result: Tile[]) {
  if (nodes.length === 0) return
  if (nodes.length === 1) {
    result.push({ node: nodes[0], ...bounds })
    return
  }
  const total = nodes.reduce((sum, node) => sum + Math.max(1, node.sizeBytes), 0)
  let leftTotal = 0
  let splitIndex = 1
  for (; splitIndex < nodes.length; splitIndex++) {
    const next = leftTotal + Math.max(1, nodes[splitIndex - 1].sizeBytes)
    if (next > total / 2 && splitIndex > 1) break
    leftTotal = next
  }
  if (leftTotal === 0) leftTotal = Math.max(1, nodes[0].sizeBytes)
  const ratio = Math.min(.92, Math.max(.08, leftTotal / total))
  const first = nodes.slice(0, splitIndex)
  const second = nodes.slice(splitIndex)
  if (bounds.width >= bounds.height) {
    const width = bounds.width * ratio
    split(first, { ...bounds, width }, result)
    split(second, { x: bounds.x + width, y: bounds.y, width: bounds.width - width, height: bounds.height }, result)
  } else {
    const height = bounds.height * ratio
    split(first, { ...bounds, height }, result)
    split(second, { x: bounds.x, y: bounds.y + height, width: bounds.width, height: bounds.height - height }, result)
  }
}
