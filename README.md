# BILLAR EL BRUJO API V54 - PRODUCCION LIMPIA

Este paquete contiene solo los archivos necesarios para GitHub/Railway.
No incluye readmes antiguos, SQL de versiones anteriores, stock TXT ni archivos de prueba.

## Version compatible
- Caja/Admin requerida: V132 o superior.
- Sucursal 1: EL BRUJO - 8 mesas (1..7 normales, 8 privada).
- Sucursal 2: EL BRUJO PREMIU - 29 mesas.

## Tarifas iniciales V54
- Lunes: Bs 10/h (promocion activa).
- Martes a domingo: Bs 20/h.
- Mesa privada fuera de promo: Bs 20/h inicialmente.
- El administrador puede editar precio normal, promo lunes y privada desde Caja/Admin V132.
- La tarifa se congela al iniciar la sesion: cambiar precios no altera una mesa que ya esta jugando.

## Proteccion contra duplicados
- Caja V132 obligatoria para registrar cobros.
- SyncKey + OperationKey por venta.
- Candado MySQL de servidor durante cobros simultaneos.
- Un solo cierre final por SessionId.
- ConsumptionKey para impedir cobrar dos veces un mismo consumo.
- line_key y movement_key para no duplicar detalle ni descuento de inventario.
- Libro de caja transaccional.
- Los reportes de productos agrupan por producto/presentacion y no multiplican catalogos duplicados.

## Railway
Subir estos 5 archivos al repositorio de GitHub y conectar ese repositorio a Railway.
Variables necesarias:
- MYSQLHOST / MYSQLPORT / MYSQLDATABASE / MYSQLUSER / MYSQLPASSWORD (Railway MySQL)
- GOOGLE_SHEET_ID
- GOOGLE_CREDENTIALS_JSON

Comprobacion despues del deploy:
- /health -> version V54_PROMO_LUNES_ANTI_DUPLICADO
- /api/system/version -> minimumClientVersion = 132
