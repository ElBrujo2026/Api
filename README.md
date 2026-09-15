# BILLAR EL BRUJO API V60

API limpia para Railway.

## V60 - Pago de productos sin cerrar mesa
- Compatible con Caja/Admin V140 o superior.
- Permite múltiples pagos parciales legítimos en una misma sesión de mesa.
- Cada línea parcial exige `ConsumptionKey` única.
- `OperationKey` + `SyncKey` mantienen idempotencia de la venta.
- Locks MySQL por `ConsumptionKey` bloquean dos cobros simultáneos del mismo producto.
- `detalle_ventas.consumption_key` es único: un producto ya pagado no puede reaparecer en otro cobro.
- El cobro final de mesa sigue siendo único por sesión.
- Reportes, arqueos y Google Sheets leen ventas canónicas para evitar inflación histórica.

No incluye migraciones manuales ni archivos de prueba; el esquema faltante se crea de forma segura al iniciar.


V61 BUILD FIX
- Corrige CS8801 en SheetsReporter: una clase no puede invocar una función local top-level.
- Mantiene la inicialización de protección contable en el arranque y endpoints de la API.
- Actualiza Google.Apis.Sheets.v4 a 1.68.0.3658 para evitar la advertencia de versión aproximada en Railway.
- Compatible con Caja/Admin V140.
