export interface StorageNode {
  id: string
  name: string
  logicalPath: string
  kind: "directory" | "file" | "aggregate"
  origin: string
  purpose: string
  cleanupKind: "quarantine" | "protected"
  protectionReason?: string | null
  sizeBytes: number
  fileCount: number
  directoryCount: number
  modifiedAt?: string | null
  truncated: boolean
  children: StorageNode[]
}

export interface StorageArea {
  id: string
  name: string
  rootLabel: string
  kind: "physical" | "logical"
  logicalOnly: boolean
  volumeTotalBytes?: number | null
  volumeFreeBytes?: number | null
  root: StorageNode
  error?: string | null
}

export interface StorageSnapshot {
  id: string
  startedAt: string
  completedAt: string
  physicalBytes: number
  logicalBytes: number
  areas: StorageArea[]
}

export interface StorageStatus {
  progress: {
    running: boolean
    scanId?: string | null
    startedAt?: string | null
    currentArea?: string | null
    bytesVisited: number
    filesVisited: number
    directoriesVisited: number
    error?: string | null
  }
  snapshot?: StorageSnapshot | null
}

export interface CleanupPreview {
  token: string
  nodeId: string
  logicalPath: string
  name: string
  sizeBytes: number
  fileCount: number
  expiresAt: string
  action: "quarantine"
}

export interface QuarantineItem {
  id: string
  name: string
  originalLogicalPath: string
  sizeBytes: number
  fileCount: number
  directory: boolean
  quarantinedAt: string
}
