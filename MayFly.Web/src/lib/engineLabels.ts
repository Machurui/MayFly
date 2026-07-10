export const engineLabels: Record<string, string> = {
  postgres: 'PostgreSQL',
  mysql:    'MySQL',
  mariadb:  'MariaDB',
  mssql:    'SQL Server',
}

export const engineLabel = (id: string): string => engineLabels[id] ?? id
