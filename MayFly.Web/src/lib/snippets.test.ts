import { describe, it, expect } from 'vitest'
import { buildSnippets } from './snippets'
import type { InstanceDto } from '../api/types'

const inst = {
  connectionString: 'postgresql://appuser:secret@db.example.com:20005/appdb',
  publicPort: 20005, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

describe('buildSnippets', () => {
  const s = buildSnippets(inst)
  it('bash uses psql with the full URL', () => {
    expect(s.bash).toContain('psql "postgresql://appuser:secret@db.example.com:20005/appdb"')
  })
  it('python references psycopg and the host/port', () => {
    expect(s.python).toContain('psycopg')
    expect(s.python).toContain('db.example.com')
    expect(s.python).toContain('20005')
  })
  it('node uses pg Client', () => expect(s.node).toContain("new Client"))
  it('go uses pgx or connection URL', () => expect(s.go).toContain('appdb'))
  it('dotnet uses Npgsql connection string', () => expect(s.dotnet).toContain('Host=db.example.com'))
})
