# BILLAR EL BRUJO API V57 - RELEVO + TRANSFERENCIA + ANTI DUPLICADO

Archivos necesarios para Railway solamente.

Cambios V57:
- exige Caja/Admin V137 o superior;
- TRANSFERENCIA válida como método de pago;
- arqueo reconcilia efectivo, QR, transferencia y total contra ventas únicas;
- un cierre existente se vuelve a conciliar si las ventas llegaron después (sin crear un segundo cierre);
- Google Sheets muestra transferencia en VENTAS y CIERRES;
- OperationKey, SyncKey, session_id y consumption_key mantienen las defensas anti duplicado.

No borra ventas ni inventario existentes.
