import { describe, it, expect } from 'vitest'
import { buildSnippets } from './snippets'
import type { InstanceDto } from '../api/types'

const pgInst = {
  engine: 'postgres',
  connectionString: 'postgresql://appuser:secret@db.example.com:20005/appdb',
  publicPort: 20005, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

const mysqlInst = {
  engine: 'mysql',
  connectionString: 'mysql://appuser:pw@localhost:3306/appdb',
  publicPort: 3306, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

const mariadbInst = {
  engine: 'mariadb',
  connectionString: 'mysql://appuser:pw@localhost:3306/appdb',
  publicPort: 3306, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

const mssqlInst = {
  engine: 'mssql',
  connectionString: 'Server=localhost,1433;Database=appdb;User Id=appuser;Password=pw;TrustServerCertificate=True',
  publicPort: 1433, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

describe('buildSnippets – postgres', () => {
  const s = buildSnippets(pgInst)
  it('bash uses psql with the full URL', () => {
    expect(s.bash).toContain('psql "postgresql://appuser:secret@db.example.com:20005/appdb"')
  })
  it('python references psycopg and the host/port', () => {
    expect(s.python).toContain('psycopg')
    expect(s.python).toContain('db.example.com')
    expect(s.python).toContain('20005')
    expect(s.python).toContain('appuser')
    expect(s.python).toContain('secret')
  })
  it('node uses pg Client', () => {
    expect(s.node).toContain("new Client")
    expect(s.node).toContain('db.example.com')
    expect(s.node).toContain('appdb')
  })
  it('go uses pgx or connection URL', () => {
    expect(s.go).toContain('appdb')
    expect(s.go).toContain('pgx.Connect')
  })
  it('dotnet uses Npgsql connection string', () => expect(s.dotnet).toContain('Host=db.example.com'))
  it('password appears in a representative snippet', () => {
    expect(s.dotnet).toContain('secret')
  })
})

describe('buildSnippets – mysql', () => {
  const s = buildSnippets(mysqlInst)
  it('bash uses mysql CLI', () => {
    expect(s.bash).toContain('mysql')
    expect(s.bash).toContain('localhost')
    expect(s.bash).toContain('3306')
    expect(s.bash).toContain('appuser')
    expect(s.bash).toContain('appdb')
  })
  it('python uses pymysql', () => {
    expect(s.python).toContain('pymysql')
    expect(s.python).toContain('localhost')
    expect(s.python).toContain('3306')
    expect(s.python).toContain('appuser')
    expect(s.python).toContain('appdb')
  })
  it('node uses mysql2', () => {
    expect(s.node).toContain('mysql2')
    expect(s.node).toContain('localhost')
    expect(s.node).toContain('3306')
    expect(s.node).toContain('appuser')
    expect(s.node).toContain('appdb')
  })
  it('go uses go-sql-driver/mysql', () => {
    expect(s.go).toContain('go-sql-driver/mysql')
    expect(s.go).toContain('localhost')
    expect(s.go).toContain('3306')
    expect(s.go).toContain('appuser')
    expect(s.go).toContain('appdb')
  })
  it('dotnet uses MySqlConnector', () => {
    expect(s.dotnet).toContain('MySqlConnector')
    expect(s.dotnet).toContain('localhost')
    expect(s.dotnet).toContain('3306')
    expect(s.dotnet).toContain('appuser')
    expect(s.dotnet).toContain('appdb')
  })
  it('password appears in a representative snippet', () => {
    expect(s.dotnet).toContain('pw')
  })
})

describe('buildSnippets – mariadb', () => {
  const s = buildSnippets(mariadbInst)
  it('bash uses mysql CLI', () => {
    expect(s.bash).toContain('mysql')
    expect(s.bash).toContain('localhost')
    expect(s.bash).toContain('3306')
    expect(s.bash).toContain('appuser')
    expect(s.bash).toContain('appdb')
  })
  it('python uses pymysql', () => {
    expect(s.python).toContain('pymysql')
    expect(s.python).toContain('localhost')
    expect(s.python).toContain('appuser')
    expect(s.python).toContain('appdb')
  })
  it('node uses mysql2', () => {
    expect(s.node).toContain('mysql2')
    expect(s.node).toContain('localhost')
    expect(s.node).toContain('appuser')
    expect(s.node).toContain('appdb')
  })
  it('go uses go-sql-driver/mysql', () => {
    expect(s.go).toContain('go-sql-driver/mysql')
    expect(s.go).toContain('localhost')
    expect(s.go).toContain('appuser')
    expect(s.go).toContain('appdb')
  })
  it('dotnet uses MySqlConnector', () => {
    expect(s.dotnet).toContain('MySqlConnector')
    expect(s.dotnet).toContain('localhost')
    expect(s.dotnet).toContain('appuser')
    expect(s.dotnet).toContain('appdb')
  })
  it('password appears in a representative snippet', () => {
    expect(s.dotnet).toContain('pw')
  })
})

const mongoInst = {
  engine: 'mongo',
  connectionString: 'mongodb://appuser:pw@localhost:27017/appdb',
  publicPort: 27017, dbName: 'appdb', dbUser: 'appuser',
} as InstanceDto

describe('buildSnippets – mongo', () => {
  const s = buildSnippets(mongoInst)
  it('bash uses mongosh with the full URI', () => {
    expect(s.bash).toContain('mongosh')
    expect(s.bash).toContain('localhost')
    expect(s.bash).toContain('27017')
    expect(s.bash).toContain('appuser')
    expect(s.bash).toContain('appdb')
    expect(s.bash).toContain('pw')
  })
  it('python uses pymongo', () => {
    expect(s.python).toContain('pymongo')
    expect(s.python).toContain('localhost')
    expect(s.python).toContain('27017')
    expect(s.python).toContain('appuser')
    expect(s.python).toContain('appdb')
    expect(s.python).toContain('pw')
  })
  it('node uses mongodb driver', () => {
    expect(s.node).toContain('mongodb')
    expect(s.node).toContain('localhost')
    expect(s.node).toContain('27017')
    expect(s.node).toContain('appuser')
    expect(s.node).toContain('appdb')
    expect(s.node).toContain('pw')
  })
  it('go uses go.mongodb.org/mongo-driver', () => {
    expect(s.go).toContain('go.mongodb.org/mongo-driver')
    expect(s.go).toContain('localhost')
    expect(s.go).toContain('27017')
    expect(s.go).toContain('appuser')
    expect(s.go).toContain('appdb')
    expect(s.go).toContain('pw')
  })
  it('dotnet uses MongoDB.Driver', () => {
    expect(s.dotnet).toContain('MongoDB.Driver')
    expect(s.dotnet).toContain('localhost')
    expect(s.dotnet).toContain('27017')
    expect(s.dotnet).toContain('appuser')
    expect(s.dotnet).toContain('appdb')
    expect(s.dotnet).toContain('pw')
  })
})

describe('buildSnippets – mssql', () => {
  it('parse does not throw on keyword connection string', () => {
    expect(() => buildSnippets(mssqlInst)).not.toThrow()
  })
  const s = buildSnippets(mssqlInst)
  it('bash uses sqlcmd', () => {
    expect(s.bash).toContain('sqlcmd')
    expect(s.bash).toContain('localhost')
    expect(s.bash).toContain('1433')
    expect(s.bash).toContain('appuser')
    expect(s.bash).toContain('appdb')
  })
  it('python uses pyodbc', () => {
    expect(s.python).toContain('pyodbc')
    expect(s.python).toContain('localhost')
    expect(s.python).toContain('1433')
    expect(s.python).toContain('appuser')
    expect(s.python).toContain('appdb')
  })
  it('node uses mssql', () => {
    expect(s.node).toContain('mssql')
    expect(s.node).toContain('localhost')
    expect(s.node).toContain('1433')
    expect(s.node).toContain('appuser')
    expect(s.node).toContain('appdb')
  })
  it('go uses go-mssqldb', () => {
    expect(s.go).toContain('go-mssqldb')
    expect(s.go).toContain('localhost')
    expect(s.go).toContain('1433')
    expect(s.go).toContain('appuser')
    expect(s.go).toContain('appdb')
  })
  it('dotnet uses Microsoft.Data.SqlClient', () => {
    expect(s.dotnet).toContain('Microsoft.Data.SqlClient')
    expect(s.dotnet).toContain('localhost')
    expect(s.dotnet).toContain('1433')
    expect(s.dotnet).toContain('appuser')
    expect(s.dotnet).toContain('appdb')
  })
  it('password appears in a representative snippet', () => {
    expect(s.dotnet).toContain('pw')
  })
})
