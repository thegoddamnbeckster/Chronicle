Get-ChildItem -Path W:\Scripts\Chronicle -Recurse -Filter "*.db" -ErrorAction SilentlyContinue | Select-Object FullName, LastWriteTime, Length | Sort-Object LastWriteTime -Descending
