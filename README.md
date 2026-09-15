# Billar El Brujo API V55 - Cajas reales + anti duplicado

API para Railway/MySQL/Google Sheets.

## Estructura operativa

### EL BRUJO (Sucursal 1)
- 1 PC de caja.
- 2 cajeros: MAÑANA y NOCHE.
- Caja: `CAJA ÚNICA`.
- 8 mesas: 7 normales + 1 privada.

### EL BRUJO PREMIU (Sucursal 2)
- 2 PCs: `CAJA ARRIBA` y `CAJA ABAJO`.
- 2 cajeros en MAÑANA y 2 cajeros en NOCHE.
- 4 cajeros en total.
- 29 mesas.

Los usuarios de caja antiguos quedan INACTIVOS; su historial de ventas no se borra.

## Variables Railway
- `MYSQL_URL=${{MySQL.MYSQL_URL}}`
- `GOOGLE_SHEET_ID`
- `GOOGLE_CREDENTIALS_JSON`

## Reportes Google Sheets
Los reportes se escriben por sucursal. En V55 las hojas `*_VENTAS` y
`*_CIERRES` incluyen la columna `caja` para distinguir CAJA ÚNICA,
CAJA ARRIBA y CAJA ABAJO.

## Integridad contable
- Idempotencia por `sync_key` y `operation_key`.
- Reintentos de red no deben crear otra venta.
- Productos vendidos se agrupan por producto/presentacion en el reporte y no se usan para duplicar la recaudacion.
- Ganancias de EL BRUJO y EL BRUJO PREMIU no se consolidan en las hojas operativas.
