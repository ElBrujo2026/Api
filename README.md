# BILLAR EL BRUJO API V64

## Sectores + Excel online + usuarios reales EL BRUJO
- EL BRUJO: sector GENERAL.
- EL BRUJO PREMIU: ARRIBA y ABAJO con catálogo/stock independiente.
- EL BRUJO usa los usuarios: `brujo1` (MAÑANA) y `brujo2` (NOCHE).
- EL BRUJO PREMIU conserva sus 4 cajeros sin cambios.
- Google Sheets publica ventas, cierres, productos, inventario y comisiones por sucursal.
- `/api/sheets/debug` permite comprobar cuántas filas existen realmente en Railway.
- `/api/sheets/sync` fuerza la publicación a Google Sheets.
- Mantiene ventas canónicas, OperationKey, SyncKey y ConsumptionKey para evitar inflación por reintentos.

Compatible con Caja/Admin V143 y App Mesera V18.
