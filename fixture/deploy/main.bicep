resource sql 'Microsoft.Sql/servers@2023-05-01-preview' = { name: 'materials-sql' }
resource db 'Microsoft.Sql/servers/databases@2023-05-01-preview' = { name: 'materials' }
