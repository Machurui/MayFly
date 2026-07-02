import type { InstanceDto } from '../api/types'

interface Parts { user: string; pass: string; host: string; port: number; db: string; url: string }

function parse(inst: InstanceDto): Parts {
  const u = new URL(inst.connectionString)
  return {
    user: decodeURIComponent(u.username), pass: decodeURIComponent(u.password),
    host: u.hostname, port: Number(u.port), db: u.pathname.replace(/^\//, ''),
    url: inst.connectionString,
  }
}

export function buildSnippets(inst: InstanceDto): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
  const p = parse(inst)
  return {
    bash: `psql "${p.url}"`,
    python: `import psycopg
conn = psycopg.connect(host="${p.host}", port=${p.port}, dbname="${p.db}", user="${p.user}", password="${p.pass}")
with conn.cursor() as cur:
    cur.execute("SELECT 1")
    print(cur.fetchone())`,
    node: `import { Client } from 'pg'
const client = new Client({ host: '${p.host}', port: ${p.port}, database: '${p.db}', user: '${p.user}', password: '${p.pass}' })
await client.connect()
console.log((await client.query('SELECT 1')).rows)`,
    go: `package main

import (
    "context"
    "fmt"
    "github.com/jackc/pgx/v5"
)

func main() {
    conn, _ := pgx.Connect(context.Background(), "${p.url}")
    defer conn.Close(context.Background())
    var n int
    conn.QueryRow(context.Background(), "SELECT 1").Scan(&n)
    fmt.Println(n)
}`,
    dotnet: `using Npgsql;
var cs = "Host=${p.host};Port=${p.port};Database=${p.db};Username=${p.user};Password=${p.pass}";
await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
await using var cmd = new NpgsqlCommand("SELECT 1", conn);
Console.WriteLine(await cmd.ExecuteScalarAsync());`,
  }
}
