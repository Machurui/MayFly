import type { InstanceDto } from '../api/types'

interface Parts { user: string; pass: string; host: string; port: number; db: string; url: string }

function parseMssql(connStr: string): Parts {
  const pairs: Record<string, string> = {}
  connStr.split(';').forEach(seg => {
    const idx = seg.indexOf('=')
    if (idx < 0) return
    pairs[seg.slice(0, idx).trim().toLowerCase()] = seg.slice(idx + 1).trim()
  })
  const serverParts = (pairs['server'] ?? '').split(',')
  return {
    host: serverParts[0] ?? '',
    port: Number(serverParts[1] ?? '1433'),
    db: pairs['database'] ?? '',
    user: pairs['user id'] ?? '',
    pass: pairs['password'] ?? '',
    url: connStr,
  }
}

function parse(inst: InstanceDto): Parts {
  if (inst.engine === 'mssql') return parseMssql(inst.connectionString)
  const u = new URL(inst.connectionString)
  return {
    user: decodeURIComponent(u.username), pass: decodeURIComponent(u.password),
    host: u.hostname, port: Number(u.port), db: u.pathname.replace(/^\//, ''),
    url: inst.connectionString,
  }
}

function postgresSnippets(p: Parts): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
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

function mysqlSnippets(p: Parts): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
  return {
    bash: `mysql -h ${p.host} -P ${p.port} -u ${p.user} -p${p.pass} ${p.db}`,
    python: `import pymysql
conn = pymysql.connect(host="${p.host}", port=${p.port}, user="${p.user}", password="${p.pass}", database="${p.db}")
with conn.cursor() as cur:
    cur.execute("SELECT 1")
    print(cur.fetchone())`,
    node: `import mysql from 'mysql2/promise'
const conn = await mysql.createConnection({ host: '${p.host}', port: ${p.port}, user: '${p.user}', password: '${p.pass}', database: '${p.db}' })
const [rows] = await conn.execute('SELECT 1')
console.log(rows)`,
    go: `package main

import (
    "database/sql"
    "fmt"
    _ "github.com/go-sql-driver/mysql"
)

func main() {
    db, _ := sql.Open("mysql", "${p.user}:${p.pass}@tcp(${p.host}:${p.port})/${p.db}")
    defer db.Close()
    var n int
    db.QueryRow("SELECT 1").Scan(&n)
    fmt.Println(n)
}`,
    dotnet: `using MySqlConnector;
var cs = "Server=${p.host};Port=${p.port};Database=${p.db};User Id=${p.user};Password=${p.pass}";
await using var conn = new MySqlConnection(cs);
await conn.OpenAsync();
await using var cmd = new MySqlCommand("SELECT 1", conn);
Console.WriteLine(await cmd.ExecuteScalarAsync());`,
  }
}

function mssqlSnippets(p: Parts): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
  return {
    bash: `sqlcmd -S ${p.host},${p.port} -U ${p.user} -P ${p.pass} -d ${p.db} -C`,
    python: `import pyodbc
conn = pyodbc.connect("DRIVER={ODBC Driver 18 for SQL Server};SERVER=${p.host},${p.port};DATABASE=${p.db};UID=${p.user};PWD=${p.pass};TrustServerCertificate=yes")
cursor = conn.cursor()
cursor.execute("SELECT 1")
print(cursor.fetchone())`,
    node: `import sql from 'mssql'
const conn = await sql.connect({ server: '${p.host}', port: ${p.port}, database: '${p.db}', user: '${p.user}', password: '${p.pass}', options: { trustServerCertificate: true } })
const result = await conn.request().query('SELECT 1')
console.log(result.recordset)`,
    go: `package main

import (
    "database/sql"
    "fmt"
    _ "github.com/microsoft/go-mssqldb"
)

func main() {
    db, _ := sql.Open("sqlserver", "sqlserver://${p.user}:${p.pass}@${p.host}:${p.port}?database=${p.db}")
    defer db.Close()
    var n int
    db.QueryRow("SELECT 1").Scan(&n)
    fmt.Println(n)
}`,
    dotnet: `using Microsoft.Data.SqlClient;
var cs = "Server=${p.host},${p.port};Database=${p.db};User Id=${p.user};Password=${p.pass};TrustServerCertificate=True";
await using var conn = new SqlConnection(cs);
await conn.OpenAsync();
await using var cmd = new SqlCommand("SELECT 1", conn);
Console.WriteLine(await cmd.ExecuteScalarAsync());`,
  }
}

function mongoSnippets(p: Parts): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
  const uri = `mongodb://${p.user}:${p.pass}@${p.host}:${p.port}/${p.db}`
  return {
    bash: `mongosh "${uri}"`,
    python: `from pymongo import MongoClient
client = MongoClient("${uri}")
db = client["${p.db}"]`,
    node: `import { MongoClient } from 'mongodb'
const client = new MongoClient("${uri}")
await client.connect()
const db = client.db("${p.db}")`,
    go: `package main

import (
    "context"
    "go.mongodb.org/mongo-driver/mongo"
    "go.mongodb.org/mongo-driver/mongo/options"
)

func main() {
    client, _ := mongo.Connect(context.TODO(), options.Client().ApplyURI("${uri}"))
    defer client.Disconnect(context.TODO())
}`,
    dotnet: `using MongoDB.Driver;
var client = new MongoClient("${uri}");
var db = client.GetDatabase("${p.db}");`,
  }
}

export function buildSnippets(inst: InstanceDto): Record<'bash' | 'python' | 'node' | 'go' | 'dotnet', string> {
  const p = parse(inst)
  if (inst.engine === 'mongo') return mongoSnippets(p)
  if (inst.engine === 'mysql' || inst.engine === 'mariadb') return mysqlSnippets(p)
  if (inst.engine === 'mssql') return mssqlSnippets(p)
  return postgresSnippets(p)
}
