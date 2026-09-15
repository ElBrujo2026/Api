# Billar El Brujo API V56

API limpia para Railway. Version: `V56_ARQUEO_EXCEL_CAJA_ANTI_DUPLICADO`.

## Archivos necesarios
- `Program.cs`
- `BillarElBrujoApi.csproj`
- `Dockerfile`
- `railway.toml`
- `README.md`

## Correcciones V56
- Guarda `caja_nombre` y `turno` reales en cada venta.
- Backfill de ventas antiguas: turno por hora real del cobro y caja por usuario/sucursal cuando falta.
- `/api/ventas` devuelve caja y turno y aumenta el limite de lectura para reportes administrativos.
- El cierre de turno se concilia en servidor contra las ventas confirmadas de ese cajero y ventana horaria antes de guardarse.
- Google Sheets concilia cierres antiguos contra ventas reales cuando existen ventas para esa ventana.
- Mantiene proteccion por `sync_key`, `operation_key`, sesion final, `consumption_key` y movimientos de inventario idempotentes.
- Reportes de EL BRUJO y EL BRUJO PREMIU permanecen separados.

## Variables Railway
- `MYSQL_URL = ${{MySQL.MYSQL_URL}}`
- `GOOGLE_SHEET_ID`
- `GOOGLE_CREDENTIALS_JSON`

## Comprobacion
- `GET /health`
- Debe indicar MySQL conectado y Google Sheets configurado.

No subir credenciales de Google al repositorio.
