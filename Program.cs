using System.Text;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDesktopApp", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<SheetsReporter>();

var app = builder.Build();

app.UseCors("AllowDesktopApp");

// V50: arranque seguro + reportes de turno 08:00-20:00 / 20:00-08:00.
// Solo crea estructuras faltantes y aplica la configuración oficial; no borra ventas ni inventario.
try
{
    var startupDb = app.Services.GetRequiredService<Db>();
    await using var startupCon = await startupDb.OpenAsync();
    await EnsureCoreSchemaAsync(startupCon);
    await EnsureUserManagementTables(startupCon);
    await EnsureMesasEnVivoTables(startupCon);
    await EnsureOfficialBranchAndTableLayout(startupCon);
    await EnsureTablePricingAsync(startupCon);
    await EnsureVentaSyncProtection(startupCon);
    await EnsureAccountingLedger(startupCon);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "No se pudo completar la inicialización segura de MySQL.");
}

app.MapGet("/", () => Results.Ok(new
{
    app = "BILLAR EL BRUJO API",
    status = "online",
    message = "API funcionando correctamente"
}));

app.MapGet("/health", async (Db db, SheetsReporter sheets) =>
{
    try
    {
        await using var con = await db.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT DATABASE();", con);
        var database = Convert.ToString(await cmd.ExecuteScalarAsync());

        return Results.Ok(new
        {
            ok = true,
            version = "V56_ARQUEO_EXCEL_CAJA_ANTI_DUPLICADO",
            database,
            mysql = "conectado",
            googleSheets = sheets.IsConfigured ? "configurado" : "faltan variables GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo conectar a MySQL: " + ex.Message);
    }
});

app.MapGet("/api/system/version", () => Results.Ok(new
{
    ok = true,
    apiVersion = "V56_ARQUEO_EXCEL_CAJA_ANTI_DUPLICADO",
    minimumClientVersion = 132,
    accountingMode = "LIBRO_INMUTABLE_TRANSACCIONAL",
    message = "Se requiere Caja/Admin V134 para arqueo conciliado, reporte por caja en Excel y protección contable actual."
}));

app.MapGet("/api/sheets/status", (SheetsReporter sheets) =>
{
    return Results.Ok(new
    {
        configured = sheets.IsConfigured,
        spreadsheetId = sheets.SpreadsheetId,
        message = sheets.IsConfigured
            ? "Google Sheets configurado en Railway"
            : "Faltan GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON en Variables de Railway"
    });
});

app.MapPost("/api/sheets/sync", async (Db db, SheetsReporter sheets) =>
{
    if (!sheets.IsConfigured)
        return Results.BadRequest(new { ok = false, message = "Faltan GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON en Railway." });

    try
    {
        var result = await sheets.SyncFromDatabaseAsync(db);
        return Results.Ok(new { ok = true, message = result });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo actualizar Google Sheets: " + ex.Message);
    }
});

app.MapGet("/api/sheets/sync", async (Db db, SheetsReporter sheets) =>
{
    if (!sheets.IsConfigured)
        return Results.BadRequest(new { ok = false, message = "Faltan GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON en Railway." });

    try
    {
        var result = await sheets.SyncFromDatabaseAsync(db);
        return Results.Ok(new { ok = true, message = result });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo actualizar Google Sheets: " + ex.Message);
    }
});

// V49 producción: limpieza masiva deshabilitada para proteger los datos reales.
app.MapPost("/api/admin/limpiar-pruebas", () =>
    Results.Json(new { ok = false, message = "Limpieza masiva deshabilitada en producción para proteger los datos." }, statusCode: StatusCodes.Status410Gone));

app.MapGet("/api/admin/limpiar-pruebas", () =>
    Results.Json(new { ok = false, message = "Limpieza masiva deshabilitada en producción para proteger los datos." }, statusCode: StatusCodes.Status410Gone));

app.MapPost("/api/login", async (Db db, LoginRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string claveIngresada = req.Clave ?? "";

    int id = 0;
    string usuarioDb = "";
    string rol = "";
    int sucursalId = 1;
    string sucursal = "EL BRUJO";
    string nombre = "";
    string caja = "";
    string turno = "MAÑANA";
    string claveGuardada = "";

    const string sql = """
        SELECT u.id, u.usuario, u.clave, u.rol, u.estado, u.sucursal_id,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        WHERE u.usuario = @usuario AND u.estado = 'ACTIVO'
        LIMIT 1;
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@usuario", usuario);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.Unauthorized();

        id = rd.GetInt32("id");
        usuarioDb = rd.GetString("usuario");
        claveGuardada = rd.GetString("clave");
        rol = rd.GetString("rol");
        sucursalId = rd.IsDBNull(rd.GetOrdinal("sucursal_id")) ? 1 : rd.GetInt32("sucursal_id");
        sucursal = rd.IsDBNull(rd.GetOrdinal("sucursal")) ? "TODAS" : rd.GetString("sucursal");
        nombre = rd.IsDBNull(rd.GetOrdinal("nombre_completo")) ? usuarioDb : rd.GetString("nombre_completo");
        caja = rd.IsDBNull(rd.GetOrdinal("caja_nombre")) ? "" : rd.GetString("caja_nombre");
        turno = rd.IsDBNull(rd.GetOrdinal("turno")) ? "MAÑANA" : rd.GetString("turno");
    }

    if (!PasswordHasher.Verify(claveIngresada, claveGuardada))
        return Results.Unauthorized();

    if (!PasswordHasher.IsHashed(claveGuardada))
        await UpdateUserPasswordHash(con, id, claveIngresada);

    return Results.Ok(new
    {
        id,
        usuario = usuarioDb,
        rol,
        sucursal,
        nombre,
        caja,
        turno,
        sucursal_id = sucursalId
    });
});

app.MapGet("/api/admin/usuarios", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    const string sql = """
        SELECT u.id,
               u.usuario,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               u.rol,
               u.sucursal_id,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               u.estado
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        ORDER BY u.rol, u.sucursal_id, u.usuario;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>()));
});

app.MapPost("/api/admin/usuarios", async (Db db, string clave, AdminUserRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string pass = (req.Clave ?? "").Trim();
    string rol = NormalizarRol(req.Rol);
    int sucursalId = req.SucursalId <= 0 ? 1 : req.SucursalId;
    string estado = string.IsNullOrWhiteSpace(req.Estado) ? "ACTIVO" : req.Estado.Trim().ToUpperInvariant();
    string turno = NormalizarTurno(req.Turno);

    if (string.IsNullOrWhiteSpace(usuario))
        return Results.BadRequest(new { ok = false, message = "Usuario requerido." });

    string passHash;
    if (string.IsNullOrWhiteSpace(pass))
    {
        object? actual = null;
        await using (var getPass = new MySqlCommand("SELECT clave FROM usuarios WHERE usuario = @usuario LIMIT 1;", con))
        {
            getPass.Parameters.AddWithValue("@usuario", usuario);
            actual = await getPass.ExecuteScalarAsync();
        }

        passHash = actual == null ? PasswordHasher.Hash("123456") : Convert.ToString(actual) ?? PasswordHasher.Hash("123456");
        if (!PasswordHasher.IsHashed(passHash))
            passHash = PasswordHasher.Hash(passHash);
    }
    else
    {
        passHash = PasswordHasher.Hash(pass);
    }

    await using var cmd = new MySqlCommand("""
        INSERT INTO usuarios
            (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        VALUES
            (@usuario, @clave, @rol, @sucursal_id, @estado, @nombre_completo, @caja_nombre, @turno)
        ON DUPLICATE KEY UPDATE
            clave = VALUES(clave),
            rol = VALUES(rol),
            sucursal_id = VALUES(sucursal_id),
            estado = VALUES(estado),
            nombre_completo = VALUES(nombre_completo),
            caja_nombre = VALUES(caja_nombre),
            turno = VALUES(turno);
    """, con);

    cmd.Parameters.AddWithValue("@usuario", usuario);
    cmd.Parameters.AddWithValue("@clave", passHash);
    cmd.Parameters.AddWithValue("@rol", rol);
    cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
    cmd.Parameters.AddWithValue("@estado", estado);
    cmd.Parameters.AddWithValue("@nombre_completo", string.IsNullOrWhiteSpace(req.NombreCompleto) ? usuario : req.NombreCompleto.Trim());
    cmd.Parameters.AddWithValue("@caja_nombre", req.CajaNombre ?? "");
    cmd.Parameters.AddWithValue("@turno", turno);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        ok = true,
        usuario,
        rol,
        sucursal_id = sucursalId,
        turno,
        estado,
        message = "Usuario guardado."
    });
});

app.MapPost("/api/admin/usuarios/{id:int}/estado", async (Db db, string clave, int id, UserEstadoRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string estado = string.IsNullOrWhiteSpace(req.Estado) ? "INACTIVO" : req.Estado.Trim().ToUpperInvariant();
    if (estado != "ACTIVO" && estado != "INACTIVO")
        return Results.BadRequest(new { ok = false, message = "Estado inválido." });

    await using var cmd = new MySqlCommand("UPDATE usuarios SET estado = @estado WHERE id = @id;", con);
    cmd.Parameters.AddWithValue("@estado", estado);
    cmd.Parameters.AddWithValue("@id", id);
    int rows = await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { ok = rows > 0, id, estado });
});



app.MapPost("/api/admin/productos/comision", async (Db db, string clave, ProductCommissionRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string nombre = (req.Nombre ?? "").Trim();
    string tipo = (req.TipoComision ?? "NINGUNA").Trim().ToUpperInvariant();
    bool genera = req.GeneraComision && req.ValorComision > 0;

    if (string.IsNullOrWhiteSpace(nombre))
        return Results.BadRequest(new { ok = false, message = "Nombre del producto requerido." });

    if (!genera)
    {
        tipo = "NINGUNA";
    }
    else if (tipo != "PORCENTAJE" && tipo != "MONTO")
    {
        return Results.BadRequest(new { ok = false, message = "Tipo de comisión inválido. Use PORCENTAJE o MONTO." });
    }

    decimal valor = genera ? req.ValorComision : 0;

    const string sql = """
        UPDATE productos
        SET genera_comision = @genera_comision,
            tipo_comision = @tipo_comision,
            valor_comision = @valor_comision
        WHERE sucursal_id = @sucursal_id
          AND LOWER(nombre) = LOWER(@nombre);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@genera_comision", genera ? 1 : 0);
    cmd.Parameters.AddWithValue("@tipo_comision", tipo);
    cmd.Parameters.AddWithValue("@valor_comision", valor);
    cmd.Parameters.AddWithValue("@sucursal_id", req.SucursalId <= 0 ? 1 : req.SucursalId);
    cmd.Parameters.AddWithValue("@nombre", nombre);

    int rows = await cmd.ExecuteNonQueryAsync();
    if (rows <= 0)
        return Results.NotFound(new { ok = false, message = "Producto no encontrado en esa sucursal." });

    return Results.Ok(new
    {
        ok = true,
        producto = nombre,
        sucursal_id = req.SucursalId <= 0 ? 1 : req.SucursalId,
        genera_comision = genera,
        tipo_comision = tipo,
        valor_comision = valor
    });
});

app.MapGet("/api/admin/productos/comision", async (Db db, string clave, int sucursalId) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT id, sucursal_id, nombre, categoria,
               COALESCE(genera_comision, 0) AS genera_comision,
               COALESCE(tipo_comision, 'NINGUNA') AS tipo_comision,
               COALESCE(valor_comision, 0) AS valor_comision
        FROM productos
        WHERE sucursal_id = @sucursal_id
          AND estado = 'ACTIVO'
        ORDER BY categoria, nombre;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursal_id"] = sucursalId <= 0 ? 1 : sucursalId
    }));
});


app.MapPost("/api/admin/productos/guardar", async (Db db, SheetsReporter sheets, string clave, AdminProductRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    int sucursalId = req.SucursalId == 2 ? 2 : 1;
    string nombre = (req.Nombre ?? "").Trim();
    if (string.IsNullOrWhiteSpace(nombre))
        return Results.BadRequest(new { ok = false, message = "Nombre del producto requerido." });

    string categoria = NormalizarCategoriaProducto(req.Categoria, nombre);
    string unidadBase = string.IsNullOrWhiteSpace(req.UnidadBase) ? "UNIDAD" : req.UnidadBase.Trim();
    string tipoEntrada = string.IsNullOrWhiteSpace(req.TipoEntrada) ? "PAQUETE" : req.TipoEntrada.Trim();
    int unidadesPorEntrada = req.UnidadesPorEntrada <= 0 ? 1 : req.UnidadesPorEntrada;
    decimal stockActual = Math.Max(0, req.StockActual);
    decimal stockMinimo = Math.Max(0, req.StockMinimo);
    decimal precioCompra = Math.Max(0, req.PrecioCompra);
    string estado = string.Equals(req.Estado, "INACTIVO", StringComparison.OrdinalIgnoreCase) ? "INACTIVO" : "ACTIVO";
    string tipoComision = req.GeneraComision ? (req.TipoComision ?? "PORCENTAJE").Trim().ToUpperInvariant() : "NINGUNA";
    decimal valorComision = req.GeneraComision ? Math.Max(0, req.ValorComision) : 0;

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await using var tx = await con.BeginTransactionAsync();

    try
    {
        long productoId = 0;

        // Si la PC ya conoce el ID online, se usa primero. Esto permite renombrar
        // un producto sin crear un duplicado en Railway.
        if (req.ProductoId.GetValueOrDefault() > 0)
        {
            await using var buscarId = new MySqlCommand("SELECT id FROM productos WHERE id = @id AND sucursal_id = @sucursal_id LIMIT 1;", con, tx);
            buscarId.Parameters.AddWithValue("@id", req.ProductoId!.Value);
            buscarId.Parameters.AddWithValue("@sucursal_id", sucursalId);
            object? foundId = await buscarId.ExecuteScalarAsync();
            if (foundId != null) productoId = Convert.ToInt64(foundId);
        }

        if (productoId <= 0)
        {
            await using var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre)) LIMIT 1;", con, tx);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@nombre", nombre);
            object? found = await buscar.ExecuteScalarAsync();
            if (found != null) productoId = Convert.ToInt64(found);
        }

        if (productoId <= 0)
        {
            await using var cmd = new MySqlCommand("""
                INSERT INTO productos
                    (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado,
                     genera_comision, tipo_comision, valor_comision, sin_limite_stock,
                     tipo_entrada, unidades_por_entrada, precio_compra)
                VALUES
                    (@sucursal_id, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, @estado,
                     @genera_comision, @tipo_comision, @valor_comision, @sin_limite_stock,
                     @tipo_entrada, @unidades_por_entrada, @precio_compra);
                SELECT LAST_INSERT_ID();
            """, con, tx);
            cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            cmd.Parameters.AddWithValue("@nombre", nombre);
            cmd.Parameters.AddWithValue("@categoria", categoria);
            cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
            cmd.Parameters.AddWithValue("@stock_actual", stockActual);
            cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
            cmd.Parameters.AddWithValue("@estado", estado);
            cmd.Parameters.AddWithValue("@genera_comision", req.GeneraComision ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_comision", tipoComision);
            cmd.Parameters.AddWithValue("@valor_comision", valorComision);
            cmd.Parameters.AddWithValue("@sin_limite_stock", req.SinLimiteStock ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_entrada", tipoEntrada);
            cmd.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
            cmd.Parameters.AddWithValue("@precio_compra", precioCompra);
            productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        else
        {
            await using var cmd = new MySqlCommand("""
                UPDATE productos
                SET nombre = @nombre,
                    categoria = @categoria,
                    unidad_base = @unidad_base,
                    stock_actual = @stock_actual,
                    stock_minimo = @stock_minimo,
                    estado = @estado,
                    genera_comision = @genera_comision,
                    tipo_comision = @tipo_comision,
                    valor_comision = @valor_comision,
                    sin_limite_stock = @sin_limite_stock,
                    tipo_entrada = @tipo_entrada,
                    unidades_por_entrada = @unidades_por_entrada,
                    precio_compra = @precio_compra
                WHERE id = @id;
            """, con, tx);
            cmd.Parameters.AddWithValue("@nombre", nombre);
            cmd.Parameters.AddWithValue("@categoria", categoria);
            cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
            cmd.Parameters.AddWithValue("@stock_actual", stockActual);
            cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
            cmd.Parameters.AddWithValue("@estado", estado);
            cmd.Parameters.AddWithValue("@genera_comision", req.GeneraComision ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_comision", tipoComision);
            cmd.Parameters.AddWithValue("@valor_comision", valorComision);
            cmd.Parameters.AddWithValue("@sin_limite_stock", req.SinLimiteStock ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_entrada", tipoEntrada);
            cmd.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
            cmd.Parameters.AddWithValue("@precio_compra", precioCompra);
            cmd.Parameters.AddWithValue("@id", productoId);
            await cmd.ExecuteNonQueryAsync();
        }

        var presentacionesGuardadas = new List<object>();

        foreach (var pres in req.Presentaciones ?? new List<AdminProductPresentationRequest>())
        {
            string presNombre = string.IsNullOrWhiteSpace(pres.Nombre) ? unidadBase : pres.Nombre.Trim();
            int cantidadBase = pres.CantidadBase <= 0 ? 1 : pres.CantidadBase;
            decimal precioVenta = Math.Max(0, pres.PrecioVenta);
            string presEstado = string.Equals(pres.Estado, "INACTIVO", StringComparison.OrdinalIgnoreCase) ? "INACTIVO" : "ACTIVO";
            long presId = 0;

            if (pres.PresentacionId.GetValueOrDefault() > 0)
            {
                await using var buscarPresId = new MySqlCommand("SELECT id FROM presentaciones WHERE id = @id AND producto_id = @producto_id LIMIT 1;", con, tx);
                buscarPresId.Parameters.AddWithValue("@id", pres.PresentacionId!.Value);
                buscarPresId.Parameters.AddWithValue("@producto_id", productoId);
                object? foundPresId = await buscarPresId.ExecuteScalarAsync();
                if (foundPresId != null) presId = Convert.ToInt64(foundPresId);
            }

            if (presId <= 0)
            {
                await using var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre)) LIMIT 1;", con, tx);
                buscarPres.Parameters.AddWithValue("@producto_id", productoId);
                buscarPres.Parameters.AddWithValue("@nombre", presNombre);
                object? foundPres = await buscarPres.ExecuteScalarAsync();
                if (foundPres != null) presId = Convert.ToInt64(foundPres);
            }

            if (presId <= 0)
            {
                await using var cmd = new MySqlCommand("""
                    INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
                    VALUES (@producto_id, @nombre, @cantidad_base, @precio_venta, @estado);
                """, con, tx);
                cmd.Parameters.AddWithValue("@producto_id", productoId);
                cmd.Parameters.AddWithValue("@nombre", presNombre);
                cmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                cmd.Parameters.AddWithValue("@precio_venta", precioVenta);
                cmd.Parameters.AddWithValue("@estado", presEstado);
                await cmd.ExecuteNonQueryAsync();
                presId = cmd.LastInsertedId;
            }
            else
            {
                await using var cmd = new MySqlCommand("""
                    UPDATE presentaciones SET nombre = @nombre, cantidad_base = @cantidad_base, precio_venta = @precio_venta, estado = @estado WHERE id = @id;
                """, con, tx);
                cmd.Parameters.AddWithValue("@nombre", presNombre);
                cmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                cmd.Parameters.AddWithValue("@precio_venta", precioVenta);
                cmd.Parameters.AddWithValue("@estado", presEstado);
                cmd.Parameters.AddWithValue("@id", presId);
                await cmd.ExecuteNonQueryAsync();
            }

            presentacionesGuardadas.Add(new { id = presId, nombre = presNombre });
        }

        await tx.CommitAsync();
        await TrySyncSheets(db, sheets);
        return Results.Ok(new { ok = true, id = productoId, sucursalId, nombre, categoria, presentaciones = presentacionesGuardadas, message = "Producto guardado y sincronizado." });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo guardar el producto: " + ex.Message);
    }
});

app.MapGet("/api/admin/productos/detalle", async (Db db, string clave, int sucursalId) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT p.id AS producto_id, p.sucursal_id, p.nombre, p.categoria, p.unidad_base,
               p.stock_actual, p.stock_minimo, COALESCE(p.sin_limite_stock,0) AS sin_limite_stock,
               COALESCE(p.tipo_entrada,'PAQUETE') AS tipo_entrada,
               COALESCE(p.unidades_por_entrada,1) AS unidades_por_entrada,
               COALESCE(p.precio_compra,0) AS precio_compra,
               COALESCE(p.genera_comision,0) AS genera_comision,
               COALESCE(p.tipo_comision,'NINGUNA') AS tipo_comision,
               COALESCE(p.valor_comision,0) AS valor_comision,
               p.estado,
               pr.id AS presentacion_id, pr.nombre AS presentacion,
               pr.cantidad_base, pr.precio_venta, pr.estado AS presentacion_estado
        FROM productos p
        INNER JOIN (
            SELECT sucursal_id, LOWER(TRIM(nombre)) AS nombre_norm,
                   COALESCE(MIN(CASE WHEN estado='ACTIVO' THEN id END), MIN(id)) AS id_canonico
            FROM productos
            GROUP BY sucursal_id, LOWER(TRIM(nombre))
        ) canon ON canon.id_canonico = p.id
        LEFT JOIN presentaciones pr ON pr.producto_id = p.id
        WHERE p.sucursal_id = @sucursal_id
        ORDER BY p.categoria, p.nombre, pr.cantidad_base;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursal_id"] = sucursalId == 2 ? 2 : 1
    }));
});

app.MapPost("/api/app-mesera/login", async (Db db, LoginRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);
    await EnsureAppMeseraTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string claveIngresada = req.Clave ?? "";

    int id = 0;
    int sucursalId = 1;
    string usuarioDb = "";
    string rol = "";
    string sucursal = "EL BRUJO";
    string nombre = "";
    string turno = "MAÑANA";
    string claveGuardada = "";

    const string sql = """
        SELECT u.id, u.usuario, u.clave, u.rol, u.estado, u.sucursal_id,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        WHERE u.usuario = @usuario AND u.estado = 'ACTIVO'
        LIMIT 1;
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@usuario", usuario);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.Unauthorized();

        id = rd.GetInt32("id");
        usuarioDb = rd.GetString("usuario");
        claveGuardada = rd.GetString("clave");
        rol = rd.GetString("rol");
        sucursalId = rd.IsDBNull(rd.GetOrdinal("sucursal_id")) ? 1 : rd.GetInt32("sucursal_id");
        sucursal = rd.IsDBNull(rd.GetOrdinal("sucursal")) ? "EL BRUJO" : rd.GetString("sucursal");
        nombre = rd.IsDBNull(rd.GetOrdinal("nombre_completo")) ? usuarioDb : rd.GetString("nombre_completo");
        turno = rd.IsDBNull(rd.GetOrdinal("turno")) ? "MAÑANA" : rd.GetString("turno");
    }

    if (!PasswordHasher.Verify(claveIngresada, claveGuardada))
        return Results.Unauthorized();

    if (!rol.Contains("MESERA", StringComparison.OrdinalIgnoreCase) &&
        !rol.Contains("MESERO", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { ok = false, message = "Este usuario no tiene rol de mesera." });
    }

    if (!PasswordHasher.IsHashed(claveGuardada))
        await UpdateUserPasswordHash(con, id, claveIngresada);

    return Results.Ok(new
    {
        ok = true,
        id,
        usuario = usuarioDb,
        nombre,
        rol,
        turno,
        sucursal_id = sucursalId,
        sucursal
    });
});

app.MapGet("/api/app-mesera/mesas", async (Db db, int sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);
    await EnsureOfficialBranchAndTableLayout(con);

    const string sql = """
        SELECT m.id AS mesa_id,
               m.nombre AS mesa,
               COALESCE(me.estado, m.estado, 'LIBRE') AS estado,
               COALESCE(me.total_consumo, 0) AS total_consumo,
               me.fin_programado,
               me.cajero
        FROM mesas m
        LEFT JOIN mesa_estados me
            ON me.sucursal_id = m.sucursal_id AND me.mesa_id = m.id
        WHERE m.sucursal_id = @sucursalId
          AND m.estado <> 'INACTIVA'
        ORDER BY m.id;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapGet("/api/app-mesera/productos", async (Db db, int sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT p.id AS producto_id,
               p.nombre AS producto,
               p.categoria,
               p.stock_actual,
               pr.id AS presentacion_id,
               COALESCE(pr.nombre, 'UNIDAD') AS presentacion,
               COALESCE(pr.precio_venta, 0) AS precio,
               COALESCE(p.genera_comision, 0) AS genera_comision,
               COALESCE(p.tipo_comision, 'NINGUNA') AS tipo_comision,
               COALESCE(p.valor_comision, 0) AS valor_comision
        FROM productos p
        INNER JOIN (
            SELECT sucursal_id, LOWER(TRIM(nombre)) AS nombre_norm,
                   COALESCE(MIN(CASE WHEN estado='ACTIVO' THEN id END), MIN(id)) AS id_canonico
            FROM productos
            GROUP BY sucursal_id, LOWER(TRIM(nombre))
        ) canon ON canon.id_canonico = p.id
        LEFT JOIN presentaciones pr ON pr.producto_id = p.id AND pr.estado = 'ACTIVO'
        WHERE p.sucursal_id = @sucursalId
          AND p.estado = 'ACTIVO'
        ORDER BY
            CASE
                WHEN p.categoria = 'Agua' THEN 1
                WHEN p.categoria = 'Energizantes' THEN 2
                WHEN p.categoria = 'Sodas' THEN 3
                WHEN p.categoria IN ('Cocas', 'Coca machucada') THEN 4
                WHEN p.categoria = 'Cervezas' THEN 5
                WHEN p.categoria = 'Tragos / Botellas' THEN 6
                WHEN p.categoria = 'Servidos en vaso' THEN 7
                WHEN p.categoria = 'Cigarros' THEN 8
                WHEN p.categoria = 'Snacks y piqueos' THEN 9
                WHEN p.categoria = 'Dulces y golosinas' THEN 10
                WHEN p.categoria = 'Combos / Promos' THEN 11
                WHEN p.categoria = 'Accesorios' THEN 12
                WHEN p.categoria = 'Ceniceros' THEN 13
                WHEN p.categoria IN ('Otros', 'Otros / Extras', 'Varios') THEN 14
                ELSE 99
            END,
            p.nombre, pr.nombre;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapGet("/api/app-mesera/test", async (Db db, int sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);

    long productos = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM productos WHERE sucursal_id = " + sucursalId + " AND estado = 'ACTIVO';", con).ExecuteScalarAsync() ?? 0);
    long presentaciones = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM presentaciones pr INNER JOIN productos p ON p.id = pr.producto_id WHERE p.sucursal_id = " + sucursalId + " AND pr.estado = 'ACTIVO';", con).ExecuteScalarAsync() ?? 0);
    long mesasVivas = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM mesa_estados WHERE sucursal_id = " + sucursalId + ";", con).ExecuteScalarAsync() ?? 0);
    long pedidosPendientes = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM pedidos_movil WHERE sucursal_id = " + sucursalId + " AND estado = 'PENDIENTE';", con).ExecuteScalarAsync() ?? 0);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        sucursalId,
        productos,
        presentaciones,
        mesasVivas,
        pedidosPendientes
    });
});

app.MapPost("/api/app-mesera/pedidos", async (Db db, AppPedidoMovilRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);

    bool esCortesia =
        (req.Observacion ?? "").StartsWith("CORTESIA_MESA", StringComparison.OrdinalIgnoreCase) ||
        (req.MesaId <= 0 && (req.Mesa ?? "").Contains("CORTES", StringComparison.OrdinalIgnoreCase)); // compatibilidad con pedidos antiguos

    if (req.Cantidad <= 0)
        return Results.BadRequest(new { ok = false, message = "Cantidad inválida." });

    // V40: TODOS los pedidos de la App Mesera deben pertenecer a una mesa activa.
    if (req.MesaId <= 0)
        return Results.BadRequest(new { ok = false, message = "El pedido debe cargarse a una mesa activa." });

    const string mesaActivaSql = """
        SELECT estado
        FROM mesa_estados
        WHERE sucursal_id = @sucursal_id
          AND mesa_id = @mesa_id
        LIMIT 1;
    """;

    await using (var mesaCmd = new MySqlCommand(mesaActivaSql, con))
    {
        mesaCmd.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
        mesaCmd.Parameters.AddWithValue("@mesa_id", req.MesaId);
        string estadoMesa = Convert.ToString(await mesaCmd.ExecuteScalarAsync()) ?? "";

        if (string.IsNullOrWhiteSpace(estadoMesa) ||
            estadoMesa.Equals("LIBRE", StringComparison.OrdinalIgnoreCase) ||
            estadoMesa.Equals("RESERVADA", StringComparison.OrdinalIgnoreCase) ||
            estadoMesa.Equals("INACTIVA", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { ok = false, message = "La mesa seleccionada ya no está en juego. Actualiza las mesas e inténtalo otra vez." });
        }
    }

    // El servidor impone precio, presentación y disponibilidad reales del catálogo.
    const string validarSql = """
        SELECT p.id,
               p.nombre,
               p.categoria,
               p.stock_actual,
               COALESCE(p.sin_limite_stock, 0) AS sin_limite_stock,
               COALESCE(pr.cantidad_base, 1) AS cantidad_base,
               COALESCE(
                   pr.precio_venta,
                   (SELECT pr2.precio_venta
                      FROM presentaciones pr2
                     WHERE pr2.producto_id = p.id
                       AND pr2.estado = 'ACTIVO'
                     ORDER BY pr2.id
                     LIMIT 1),
                   0
               ) AS precio_catalogo
          FROM productos p
          LEFT JOIN presentaciones pr
                 ON pr.id = @presentacion_id
                AND pr.producto_id = p.id
                AND pr.estado = 'ACTIVO'
         WHERE p.id = @id
           AND p.sucursal_id = @sucursal_id
           AND p.estado = 'ACTIVO'
         LIMIT 1;
    """;

    decimal precioCatalogo = 0M;
    decimal stockActual = 0M;
    decimal cantidadBase = 1M;
    bool sinLimiteStock = false;

    await using (var validar = new MySqlCommand(validarSql, con))
    {
        validar.Parameters.AddWithValue("@id", req.ProductoId);
        validar.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        validar.Parameters.AddWithValue("@sucursal_id", req.SucursalId);

        await using var rd = await validar.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.BadRequest(new { ok = false, message = "Producto no encontrado o inactivo." });

        precioCatalogo = rd.IsDBNull(rd.GetOrdinal("precio_catalogo")) ? 0M : rd.GetDecimal(rd.GetOrdinal("precio_catalogo"));
        stockActual = rd.IsDBNull(rd.GetOrdinal("stock_actual")) ? 0M : rd.GetDecimal(rd.GetOrdinal("stock_actual"));
        cantidadBase = rd.IsDBNull(rd.GetOrdinal("cantidad_base")) ? 1M : rd.GetDecimal(rd.GetOrdinal("cantidad_base"));
        sinLimiteStock = !rd.IsDBNull(rd.GetOrdinal("sin_limite_stock")) && rd.GetInt32(rd.GetOrdinal("sin_limite_stock")) == 1;
    }

    if (precioCatalogo <= 0)
        return Results.BadRequest(new { ok = false, message = "El producto no tiene precio de venta válido." });

    decimal unidadesSolicitadas = Math.Max(1M, cantidadBase) * req.Cantidad;
    if (!sinLimiteStock && stockActual < unidadesSolicitadas)
    {
        return Results.Conflict(new
        {
            ok = false,
            code = "STOCK_INSUFICIENTE",
            message = "Stock insuficiente para completar el pedido.",
            disponible = stockActual,
            solicitado = unidadesSolicitadas
        });
    }

    decimal precioAplicado = precioCatalogo;
    decimal subtotal = req.Cantidad * precioAplicado;
    // V40: solo la invitación/cortesía a la mesera genera comisión fija de Bs. 5 por unidad.
    bool generaComisionAplicada = esCortesia;
    string tipoComisionAplicada = esCortesia ? "MONTO" : "NINGUNA";
    decimal valorComisionAplicada = esCortesia ? 5M : 0M;
    string syncKey = string.IsNullOrWhiteSpace(req.SyncKey) ? Guid.NewGuid().ToString("N") : req.SyncKey;

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        bool pedidoYaExistia;
        await using (var existePedido = new MySqlCommand("SELECT COUNT(*) FROM pedidos_movil WHERE sync_key = @sync_key;", con, tx))
        {
            existePedido.Parameters.AddWithValue("@sync_key", syncKey);
            pedidoYaExistia = Convert.ToInt32(await existePedido.ExecuteScalarAsync() ?? 0) > 0;
        }

        // Reserva/descuenta el inventario UNA sola vez al recibir un pedido móvil nuevo.
        // Así dos celulares no pueden vender la última unidad al mismo tiempo y el cierre de mesa
        // no vuelve a descontar el mismo producto.
        if (!pedidoYaExistia && !sinLimiteStock)
        {
            await using var reservarStock = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = stock_actual - @unidades
                WHERE id = @producto_id
                  AND sucursal_id = @sucursal_id
                  AND estado = 'ACTIVO'
                  AND COALESCE(sin_limite_stock, 0) = 0
                  AND stock_actual >= @unidades;
            """, con, tx);
            reservarStock.Parameters.AddWithValue("@unidades", unidadesSolicitadas);
            reservarStock.Parameters.AddWithValue("@producto_id", req.ProductoId);
            reservarStock.Parameters.AddWithValue("@sucursal_id", req.SucursalId);

            int filas = await reservarStock.ExecuteNonQueryAsync();
            if (filas != 1)
            {
                await tx.RollbackAsync();
                return Results.Conflict(new
                {
                    ok = false,
                    code = "STOCK_INSUFICIENTE",
                    message = "Stock insuficiente. Otro pedido pudo haber usado las últimas unidades. Actualiza el catálogo e inténtalo otra vez."
                });
            }
        }

        const string pedidoSql = """
            INSERT INTO pedidos_movil
                (sucursal_id, mesa_id, mesa, mesera_usuario, mesera_nombre, fecha, estado, total, observacion, sync_key)
            VALUES
                (@sucursal_id, @mesa_id, @mesa, @mesera_usuario, @mesera_nombre, NOW(), 'PENDIENTE', @total, @observacion, @sync_key)
            ON DUPLICATE KEY UPDATE
                total = VALUES(total),
                observacion = VALUES(observacion);
            SELECT id FROM pedidos_movil WHERE sync_key = @sync_key LIMIT 1;
        """;

        await using var pedidoCmd = new MySqlCommand(pedidoSql, con, tx);
        pedidoCmd.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
        pedidoCmd.Parameters.AddWithValue("@mesa_id", req.MesaId);
        pedidoCmd.Parameters.AddWithValue("@mesa", req.Mesa);
        pedidoCmd.Parameters.AddWithValue("@mesera_usuario", req.MeseraUsuario);
        pedidoCmd.Parameters.AddWithValue("@mesera_nombre", req.MeseraNombre);
        pedidoCmd.Parameters.AddWithValue("@total", subtotal);
        pedidoCmd.Parameters.AddWithValue("@observacion", req.Observacion ?? "");
        pedidoCmd.Parameters.AddWithValue("@sync_key", syncKey);

        long pedidoId = Convert.ToInt64(await pedidoCmd.ExecuteScalarAsync());

        await using (var del = new MySqlCommand("DELETE FROM detalle_pedidos_movil WHERE pedido_id = @pedido_id;", con, tx))
        {
            del.Parameters.AddWithValue("@pedido_id", pedidoId);
            await del.ExecuteNonQueryAsync();
        }

        const string detSql = """
            INSERT INTO detalle_pedidos_movil
                (pedido_id, producto_id, presentacion_id, producto, presentacion, cantidad, precio_unitario, subtotal,
                 genera_comision, tipo_comision, valor_comision, comision_calculada)
            VALUES
                (@pedido_id, @producto_id, @presentacion_id, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal,
                 @genera_comision, @tipo_comision, @valor_comision, @comision_calculada);
        """;

        decimal comision = generaComisionAplicada
            ? (tipoComisionAplicada.Equals("PORCENTAJE", StringComparison.OrdinalIgnoreCase)
                ? subtotal * (valorComisionAplicada / 100M)
                : valorComisionAplicada * req.Cantidad)
            : 0M;

        await using var detCmd = new MySqlCommand(detSql, con, tx);
        detCmd.Parameters.AddWithValue("@pedido_id", pedidoId);
        detCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
        detCmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        detCmd.Parameters.AddWithValue("@producto", req.Producto);
        detCmd.Parameters.AddWithValue("@presentacion", req.Presentacion);
        detCmd.Parameters.AddWithValue("@cantidad", req.Cantidad);
        detCmd.Parameters.AddWithValue("@precio_unitario", precioAplicado);
        detCmd.Parameters.AddWithValue("@subtotal", subtotal);
        detCmd.Parameters.AddWithValue("@genera_comision", generaComisionAplicada);
        detCmd.Parameters.AddWithValue("@tipo_comision", tipoComisionAplicada);
        detCmd.Parameters.AddWithValue("@valor_comision", valorComisionAplicada);
        detCmd.Parameters.AddWithValue("@comision_calculada", comision);
        await detCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();

        return Results.Ok(new
        {
            ok = true,
            pedido_id = pedidoId,
            estado = "PENDIENTE",
            total = subtotal,
            comision_calculada = comision,
            message = esCortesia ? "Cortesía recibida. Se cargará automáticamente a la mesa activa." : "Pedido recibido. Se cargará automáticamente a la mesa activa."
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo registrar el pedido móvil: " + ex.Message);
    }
});


app.MapPost("/api/app-mesera/reportes-producto", async (Db db, ProductReportRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    if (req.Cantidad <= 0)
        return Results.BadRequest(new { ok = false, message = "Cantidad inválida." });

    string motivo = string.IsNullOrWhiteSpace(req.Motivo) ? "DAÑADO/PERDIDO" : req.Motivo.Trim().ToUpperInvariant();
    string syncKey = string.IsNullOrWhiteSpace(req.SyncKey) ? Guid.NewGuid().ToString("N") : req.SyncKey;
    decimal costo = Math.Max(0, req.Cantidad * req.PrecioUnitario);
    int sucursalId = req.SucursalId <= 0 ? 1 : req.SucursalId;

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        // INSERT IGNORE hace que reintentar el mismo syncKey no vuelva a descontar inventario.
        const string sql = """
            INSERT IGNORE INTO reportes_productos_movil
                (sucursal_id, turno, usuario, nombre, fecha, producto_id, presentacion_id,
                 producto, presentacion, cantidad, precio_unitario, costo_perdido, motivo, observacion, sync_key)
            VALUES
                (@sucursal_id, @turno, @usuario, @nombre, NOW(), @producto_id, @presentacion_id,
                 @producto, @presentacion, @cantidad, @precio_unitario, @costo_perdido, @motivo, @observacion, @sync_key);
        """;

        await using var cmd = new MySqlCommand(sql, con, tx);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@turno", string.IsNullOrWhiteSpace(req.Turno) ? "MAÑANA" : req.Turno.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@usuario", req.Usuario ?? "");
        cmd.Parameters.AddWithValue("@nombre", req.Nombre ?? "");
        cmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
        cmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        cmd.Parameters.AddWithValue("@producto", req.Producto ?? "");
        cmd.Parameters.AddWithValue("@presentacion", req.Presentacion ?? "");
        cmd.Parameters.AddWithValue("@cantidad", req.Cantidad);
        cmd.Parameters.AddWithValue("@precio_unitario", Math.Max(0, req.PrecioUnitario));
        cmd.Parameters.AddWithValue("@costo_perdido", costo);
        cmd.Parameters.AddWithValue("@motivo", motivo);
        cmd.Parameters.AddWithValue("@observacion", req.Observacion ?? "");
        cmd.Parameters.AddWithValue("@sync_key", syncKey);
        int insertado = await cmd.ExecuteNonQueryAsync();

        // Para un producto físico roto/perdido, el inventario real baja también.
        // Los reportes de infraestructura usan producto_id=0 y no tocan inventario.
        if (insertado == 1 && req.ProductoId > 0)
        {
            decimal factor = 1M;
            if (req.PresentacionId > 0)
            {
                await using var factorCmd = new MySqlCommand("""
                    SELECT COALESCE(cantidad_base, 1)
                    FROM presentaciones
                    WHERE id=@presentacion_id AND producto_id=@producto_id
                    LIMIT 1;
                """, con, tx);
                factorCmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
                factorCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
                object? factorObj = await factorCmd.ExecuteScalarAsync();
                if (factorObj != null && factorObj != DBNull.Value)
                    factor = Math.Max(1M, Convert.ToDecimal(factorObj));
            }

            decimal unidades = Math.Max(0, req.Cantidad * factor);
            await using var stockCmd = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = CASE
                    WHEN COALESCE(sin_limite_stock, 0) = 1 OR categoria = 'Servidos en vaso'
                    THEN stock_actual
                    ELSE GREATEST(stock_actual - @unidades, 0)
                END
                WHERE id=@producto_id AND sucursal_id=@sucursal_id;
            """, con, tx);
            stockCmd.Parameters.AddWithValue("@unidades", unidades);
            stockCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
            stockCmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            await stockCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return Results.Ok(new
        {
            ok = true,
            costo_perdido = costo,
            duplicado = insertado == 0,
            message = req.ProductoId > 0 ? "Reporte registrado e inventario actualizado." : "Reporte de daño del establecimiento registrado."
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo registrar el reporte: " + ex.Message);
    }
});

app.MapGet("/api/admin/productos-reportados", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT r.id, r.sucursal_id,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               r.turno, r.usuario, r.nombre, r.fecha,
               r.producto, r.presentacion, r.cantidad, r.precio_unitario,
               r.costo_perdido, r.motivo, r.observacion
        FROM reportes_productos_movil r
        LEFT JOIN sucursales s ON s.id = r.sucursal_id
        ORDER BY r.fecha DESC;
    """;

    return Results.Ok(await db.QueryAsync(con, sql));
});


app.MapGet("/api/app-mesera/pedidos-pendientes", async (Db db, int sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT p.id, p.sucursal_id, p.mesa_id, p.mesa, p.mesera_usuario, p.mesera_nombre,
               p.fecha, p.estado,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN COALESCE((
                       SELECT SUM(dd.cantidad * COALESCE(ppr.precio_venta, dd.precio_unitario, 0))
                       FROM detalle_pedidos_movil dd
                       LEFT JOIN presentaciones ppr
                              ON ppr.id = dd.presentacion_id
                             AND ppr.producto_id = dd.producto_id
                             AND ppr.estado = 'ACTIVO'
                       WHERE dd.pedido_id = p.id
                   ), p.total)
                   ELSE p.total
               END AS total,
               p.observacion,
               d.producto_id, d.presentacion_id, d.producto, d.presentacion, d.cantidad,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN COALESCE(pr.precio_venta, d.precio_unitario, 0)
                   ELSE d.precio_unitario
               END AS precio_unitario,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN d.cantidad * COALESCE(pr.precio_venta, d.precio_unitario, 0)
                   ELSE d.subtotal
               END AS subtotal,
               d.genera_comision, d.tipo_comision, d.valor_comision,
               d.comision_calculada
        FROM pedidos_movil p
        INNER JOIN detalle_pedidos_movil d ON d.pedido_id = p.id
        LEFT JOIN presentaciones pr
               ON pr.id = d.presentacion_id
              AND pr.producto_id = d.producto_id
              AND pr.estado = 'ACTIVO'
        WHERE p.sucursal_id = @sucursalId
          AND p.estado = 'PENDIENTE'
        ORDER BY p.fecha;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    }));
});

app.MapPost("/api/app-mesera/pedidos/{id:long}/estado", async (Db db, SheetsReporter sheets, long id, PedidoEstadoRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string estado = (req.Estado ?? "").Trim().ToUpperInvariant();
    if (estado != "ACEPTADO" && estado != "RECHAZADO" && estado != "ENTREGADO")
        return Results.BadRequest(new { ok = false, message = "Estado inválido." });

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        const string updateSql = """
            UPDATE pedidos_movil
            SET estado = @estado,
                cajero_usuario = @cajero,
                fecha_respuesta = NOW()
            WHERE id = @id;
        """;

        await using var cmd = new MySqlCommand(updateSql, con, tx);
        cmd.Parameters.AddWithValue("@estado", estado);
        cmd.Parameters.AddWithValue("@cajero", req.CajeroUsuario ?? "");
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();

        if (estado == "ACEPTADO" || estado == "ENTREGADO")
        {
            // V36: corrige también cortesías antiguas que fueron guardadas en Bs. 0 por V35.
            const string fixCourtesyDetailSql = """
                UPDATE detalle_pedidos_movil d
                INNER JOIN pedidos_movil p ON p.id = d.pedido_id
                LEFT JOIN presentaciones pr
                       ON pr.id = d.presentacion_id
                      AND pr.producto_id = d.producto_id
                      AND pr.estado = 'ACTIVO'
                SET d.precio_unitario = COALESCE(pr.precio_venta, d.precio_unitario, 0),
                    d.subtotal = d.cantidad * COALESCE(pr.precio_venta, d.precio_unitario, 0)
                WHERE p.id = @id
                  AND (
                      UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%'
                      OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%')
                  );
            """;

            await using (var fixDetail = new MySqlCommand(fixCourtesyDetailSql, con, tx))
            {
                fixDetail.Parameters.AddWithValue("@id", id);
                await fixDetail.ExecuteNonQueryAsync();
            }

            const string fixCourtesyTotalSql = """
                UPDATE pedidos_movil p
                SET p.total = COALESCE((
                    SELECT SUM(d.subtotal)
                    FROM detalle_pedidos_movil d
                    WHERE d.pedido_id = p.id
                ), p.total)
                WHERE p.id = @id
                  AND (
                      UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%'
                      OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%')
                  );
            """;

            await using (var fixTotal = new MySqlCommand(fixCourtesyTotalSql, con, tx))
            {
                fixTotal.Parameters.AddWithValue("@id", id);
                await fixTotal.ExecuteNonQueryAsync();
            }

            const string comSql = """
                INSERT INTO comisiones_meseras
                    (pedido_id, sucursal_id, mesa_id, mesera_usuario, mesera_nombre, fecha,
                     producto, cantidad, venta_total, comision_total, estado)
                SELECT p.id, p.sucursal_id, p.mesa_id, p.mesera_usuario, p.mesera_nombre, NOW(),
                       d.producto, d.cantidad, d.subtotal, d.comision_calculada, 'PENDIENTE_PAGO'
                FROM pedidos_movil p
                INNER JOIN detalle_pedidos_movil d ON d.pedido_id = p.id
                WHERE p.id = @id AND d.comision_calculada > 0
                ON DUPLICATE KEY UPDATE
                    venta_total = VALUES(venta_total),
                    comision_total = VALUES(comision_total),
                    estado = VALUES(estado);
            """;

            await using var comCmd = new MySqlCommand(comSql, con, tx);
            comCmd.Parameters.AddWithValue("@id", id);
            await comCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        await TrySyncSheets(db, sheets);

        return Results.Ok(new { ok = true, pedido_id = id, estado });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo cambiar estado del pedido: " + ex.Message);
    }
});

app.MapGet("/api/app-mesera/comisiones", async (Db db, int sucursalId, string? meseraUsuario) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT sucursal_id, mesera_usuario, mesera_nombre, DATE(fecha) AS fecha,
               SUM(venta_total) AS total_vendido,
               SUM(comision_total) AS total_comision
        FROM comisiones_meseras
        WHERE sucursal_id = @sucursalId
          AND (@meseraUsuario IS NULL OR mesera_usuario = @meseraUsuario)
        GROUP BY sucursal_id, mesera_usuario, mesera_nombre, DATE(fecha)
        ORDER BY fecha DESC, mesera_nombre;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId,
        ["@meseraUsuario"] = meseraUsuario
    }));
});

app.MapGet("/api/sucursales", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);
    await EnsureOfficialBranchAndTableLayout(con);
    var rows = await db.QueryAsync(con, "SELECT id, nombre, direccion, estado FROM sucursales ORDER BY id;");
    return Results.Ok(rows);
});

app.MapGet("/api/config/tarifa-mesas", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    var pricing = await EnsureTablePricingAsync(con);
    return Results.Ok(new
    {
        ok = true,
        // Compatibilidad: precioHora sigue siendo la tarifa NORMAL.
        precioHora = pricing.normal,
        precioNormal = pricing.normal,
        precioPromoLunes = pricing.promoLunes,
        precioPrivada = pricing.privada,
        promoLunesActiva = pricing.promoActiva
    });
});

app.MapPost("/api/admin/tarifa-mesas", async (Db db, string clave, TableRateRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    decimal normal = req.PrecioNormal > 0 ? req.PrecioNormal : req.PrecioHora;
    decimal promo = req.PrecioPromoLunes > 0 ? req.PrecioPromoLunes : 10m;
    decimal privada = req.PrecioPrivada > 0 ? req.PrecioPrivada : 20m;
    if (normal <= 0 || promo <= 0 || privada <= 0)
        return Results.BadRequest(new { ok = false, message = "Las tarifas NORMAL, PROMO LUNES y PRIVADA deben ser mayores a 0." });

    normal = Math.Round(normal, 2);
    promo = Math.Round(promo, 2);
    privada = Math.Round(privada, 2);

    await using var con = await db.OpenAsync();
    await EnsureTablePricingAsync(con);

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        async Task SaveValue(string key, decimal value)
        {
            await using var cmd = new MySqlCommand("""
                INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado)
                VALUES (@clave, @valor, NOW())
                ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
            """, con, tx);
            cmd.Parameters.AddWithValue("@clave", key);
            cmd.Parameters.AddWithValue("@valor", value);
            await cmd.ExecuteNonQueryAsync();
        }

        await SaveValue("TARIFA_MESA_HORA", normal);
        await SaveValue("TARIFA_MESA_NORMAL", normal);
        await SaveValue("TARIFA_MESA_PROMO_LUNES", promo);
        await SaveValue("TARIFA_MESA_PRIVADA", privada);
        await SaveValue("PROMO_LUNES_ACTIVA", req.PromoLunesActiva ? 1m : 0m);

        await using (var mesas = new MySqlCommand("""
            UPDATE mesas
            SET precio_hora = CASE
                WHEN UPPER(TRIM(COALESCE(tipo_mesa,'NORMAL')))='PRIVADA' THEN @privada
                ELSE @normal
            END;
        """, con, tx))
        {
            mesas.Parameters.AddWithValue("@normal", normal);
            mesas.Parameters.AddWithValue("@privada", privada);
            await mesas.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudieron guardar las tarifas: " + ex.Message);
    }

    return Results.Ok(new
    {
        ok = true,
        precioHora = normal,
        precioNormal = normal,
        precioPromoLunes = promo,
        precioPrivada = privada,
        promoLunesActiva = req.PromoLunesActiva,
        message = "Precios guardados. La promo se aplica solo al iniciar una mesa en lunes; las sesiones abiertas conservan su tarifa original."
    });
});

app.MapGet("/api/mesas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);
    await EnsureOfficialBranchAndTableLayout(con);

    const string sql = """
        SELECT m.id, m.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, m.nombre, m.precio_hora, m.estado
        FROM mesas m
        INNER JOIN sucursales s ON s.id = m.sucursal_id
        WHERE (@sucursalId IS NULL OR m.sucursal_id = @sucursalId)
          AND m.estado <> 'INACTIVA'
        ORDER BY m.sucursal_id, m.id;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapGet("/api/productos", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT p.id, p.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, p.nombre, p.categoria,
               p.unidad_base, p.stock_actual, p.stock_minimo, COALESCE(p.sin_limite_stock, 0) AS sin_limite_stock, p.estado
        FROM productos p
        INNER JOIN (
            SELECT sucursal_id, LOWER(TRIM(nombre)) AS nombre_norm,
                   COALESCE(MIN(CASE WHEN estado='ACTIVO' THEN id END), MIN(id)) AS id_canonico
            FROM productos
            GROUP BY sucursal_id, LOWER(TRIM(nombre))
        ) canon ON canon.id_canonico = p.id
        INNER JOIN sucursales s ON s.id = p.sucursal_id
        WHERE (@sucursalId IS NULL OR p.sucursal_id = @sucursalId)
        ORDER BY p.sucursal_id,
                 CASE
                    WHEN p.categoria = 'Agua' THEN 1
                    WHEN p.categoria = 'Energizantes' THEN 2
                    WHEN p.categoria = 'Sodas' THEN 3
                    WHEN p.categoria IN ('Cocas', 'Coca machucada') THEN 4
                    WHEN p.categoria = 'Cervezas' THEN 5
                    WHEN p.categoria = 'Tragos / Botellas' THEN 6
                    WHEN p.categoria = 'Servidos en vaso' THEN 7
                    WHEN p.categoria = 'Cigarros' THEN 8
                    WHEN p.categoria = 'Snacks y piqueos' THEN 9
                    WHEN p.categoria = 'Dulces y golosinas' THEN 10
                    WHEN p.categoria = 'Combos / Promos' THEN 11
                    WHEN p.categoria = 'Accesorios' THEN 12
                    WHEN p.categoria = 'Ceniceros' THEN 13
                    WHEN p.categoria IN ('Otros', 'Otros / Extras', 'Varios') THEN 14
                    ELSE 99
                 END,
                 p.nombre;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapPost("/api/admin/cargar-catalogo-local", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoProductosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, "Unidad", 5);
            if (updated) actualizados++; else insertados++;
        }

        foreach (var item in CatalogoCombosPromosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, item.detalle, 2);
            if (updated) actualizados++; else insertados++;
        }
    }

    return Results.Ok(new
    {
        ok = true,
        version = "V19_CATALOGO_LOCAL_DULCES",
        message = "Catálogo local cargado en Railway: productos, dulces, combos y promociones.",
        insertados,
        actualizados,
        nota = "No se cargó PRUEBA porque parece dato de prueba."
    });
});

app.MapGet("/api/admin/cargar-catalogo-local", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoProductosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, "Unidad", 5);
            if (updated) actualizados++; else insertados++;
        }

        foreach (var item in CatalogoCombosPromosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, item.detalle, 2);
            if (updated) actualizados++; else insertados++;
        }
    }

    return Results.Ok(new
    {
        ok = true,
        version = "V19_CATALOGO_LOCAL_DULCES",
        message = "Catálogo local cargado en Railway: productos, dulces, combos y promociones.",
        insertados,
        actualizados,
        nota = "No se cargó PRUEBA porque parece dato de prueba."
    });
});

app.MapPost("/api/admin/cargar-catalogo-final", async (Db db, SheetsReporter sheets, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoFinalV29())
        {
            bool updated = await UpsertCatalogoFinalV29(con, sucursalId, item.nombre, item.categoria, item.cantidad, item.precio, item.sinLimiteStock);
            if (updated) actualizados++; else insertados++;
        }
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V29_CATALOGO_FINAL_STOCK",
        message = "Catálogo final cargado: cantidades, precios y productos en vaso sin límite de stock.",
        productos = CatalogoFinalV29().Length,
        sucursales = 2,
        insertados,
        actualizados,
        nota = "Los productos de categoría Servidos en vaso quedan con sin_limite_stock = 1 y no descuentan inventario."
    });
});

app.MapGet("/api/admin/cargar-catalogo-final", async (Db db, SheetsReporter sheets, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoFinalV29())
        {
            bool updated = await UpsertCatalogoFinalV29(con, sucursalId, item.nombre, item.categoria, item.cantidad, item.precio, item.sinLimiteStock);
            if (updated) actualizados++; else insertados++;
        }
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V29_CATALOGO_FINAL_STOCK",
        message = "Catálogo final cargado: cantidades, precios y productos en vaso sin límite de stock.",
        productos = CatalogoFinalV29().Length,
        sucursales = 2,
        insertados,
        actualizados,
        nota = "Los productos de categoría Servidos en vaso quedan con sin_limite_stock = 1 y no descuentan inventario."
    });
});



app.MapPost("/api/admin/aplicar-stock-inicial", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapGet("/api/admin/aplicar-stock-inicial", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapPost("/api/admin/aplicar-stock-txt", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapGet("/api/admin/aplicar-stock-txt", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapPost("/api/productos", async (Db db, SheetsReporter sheets, string clave, ProductoRequest p) =>
{
    // Endpoint legado protegido: el alta normal de productos se realiza desde
    // /api/admin/productos/guardar, que conserva toda la estructura del producto.
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (p.SucursalId != 1 && p.SucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "Sucursal inválida." });
    if (string.IsNullOrWhiteSpace(p.Nombre))
        return Results.BadRequest(new { ok = false, message = "El nombre del producto es obligatorio." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string nombre = p.Nombre.Trim();
    string categoria = NormalizarCategoriaProducto(p.Categoria, nombre);
    string unidadBase = string.IsNullOrWhiteSpace(p.UnidadBase) ? "UNIDAD" : p.UnidadBase.Trim().ToUpperInvariant();
    long id = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id=@sucursal_id AND LOWER(TRIM(nombre))=LOWER(TRIM(@nombre)) LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) id = Convert.ToInt64(found);
    }

    if (id <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado)
            VALUES (@sucursal_id, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, 'ACTIVO');
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", Math.Max(0, p.StockActual));
        cmd.Parameters.AddWithValue("@stock_minimo", Math.Max(0, p.StockMinimo));
        id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria=@categoria, unidad_base=@unidad_base,
                stock_actual=@stock_actual, stock_minimo=@stock_minimo, estado='ACTIVO'
            WHERE id=@id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", Math.Max(0, p.StockActual));
        cmd.Parameters.AddWithValue("@stock_minimo", Math.Max(0, p.StockMinimo));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    await TrySyncSheets(db, sheets);
    return Results.Ok(new { ok = true, id, categoria });
});

app.MapPost("/api/ventas", async (Db db, SheetsReporter sheets, VentaRequest venta) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureVentaSyncProtection(con);
    await EnsureAccountingLedger(con);

    // V40: conserva la división real de un pago MIXTO.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN efectivo DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN qr DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        // V45: bloqueo de cajas antiguas. Evita que una versión sin OperationKey/cola offline
        // vuelva a inflar ventas o stock.
        if (!venta.ClientVersion.HasValue || venta.ClientVersion.Value < 132)
            return Results.Json(new { ok = false, message = "Caja desactualizada. Se requiere V132 o superior para registrar cobros en Railway.", minimumClientVersion = 132 }, statusCode: StatusCodes.Status426UpgradeRequired);

        string syncKey = string.IsNullOrWhiteSpace(venta.SyncKey)
            ? Guid.NewGuid().ToString("N")
            : venta.SyncKey.Trim();
        string operationKey = string.IsNullOrWhiteSpace(venta.OperationKey)
            ? ""
            : venta.OperationKey.Trim();

        // V47: toda Caja V128+ debe traer las dos identidades. Si falta una, NO se inventa
        // una nueva en el servidor, porque eso podría transformar un reintento en otra venta.
        if (string.IsNullOrWhiteSpace(venta.SyncKey) || string.IsNullOrWhiteSpace(operationKey))
            return Results.BadRequest(new { ok = false, message = "El cobro llegó sin SyncKey u OperationKey. Se bloqueó para evitar duplicación.", minimumClientVersion = 132 });

        // V54: candados de servidor. Serializan reintentos simultáneos aunque una base histórica
        // todavía no haya podido crear todos los índices UNIQUE por duplicados antiguos.
        // La conexión libera automáticamente estos candados al terminar la solicitud.
        var guard = await AcquireSaleGuardsAsync(con, tx, venta, operationKey);
        if (!guard.ok)
            return Results.Json(new { ok = false, message = guard.message }, statusCode: StatusCodes.Status409Conflict);

        // V44: tercera defensa. Para clientes antiguos que todavía no envían OperationKey,
        // el servidor construye una huella contable determinística. De este modo, si la misma
        // venta vuelve con OTRO sync_key por una versión antigua, Railway la reconoce como la
        // misma operación y no vuelve a insertar detalle, descontar stock ni sumar recaudación.
        string legacyFingerprint = string.IsNullOrWhiteSpace(operationKey)
            ? BuildLegacyAccountingFingerprint(venta)
            : "";

        // V42: defensa estricta. Una sync_key representa una sola venta inmutable.
        // Si el mismo request llega otra vez por reintento de red, devolvemos la venta existente
        // y NO volvemos a tocar detalle, stock ni importes.
        long ventaExistenteId = 0;
        await using (var existeCmd = new MySqlCommand("""
            SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
            FROM ventas WHERE sync_key = @sync_key LIMIT 1;
        """, con, tx))
        {
            existeCmd.Parameters.AddWithValue("@sync_key", syncKey);
            await using var rd = await existeCmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                ventaExistenteId = rd.GetInt64(0);
                bool mismo = rd.GetInt32(1) == venta.SucursalId
                    && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                    && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                    && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
                if (!mismo)
                    return Results.Conflict(new { ok = false, message = "La misma sync_key ya existe con datos distintos. Se bloqueó el cobro para evitar duplicación o alteración.", syncKey, ventaId = ventaExistenteId });
            }
        }

        if (ventaExistenteId > 0)
        {
            await EnsureLedgerForExistingSaleAsync(con, tx, ventaExistenteId, venta, syncKey, operationKey);
            await tx.CommitAsync();
            return Results.Ok(new { ok = true, id = ventaExistenteId, syncKey, operationKey, duplicated = false, idempotent = true });
        }

        // V43: aunque una segunda PC genere otra sync_key, la misma operation_key
        // no puede representar dos cobros diferentes.
        if (!string.IsNullOrWhiteSpace(operationKey))
        {
            long opVentaId = 0;
            await using (var opCmd = new MySqlCommand("""
                SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
                FROM ventas WHERE operation_key = @operation_key LIMIT 1;
            """, con, tx))
            {
                opCmd.Parameters.AddWithValue("@operation_key", operationKey);
                await using var rd = await opCmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    opVentaId = rd.GetInt64(0);
                    bool mismo = rd.GetInt32(1) == venta.SucursalId
                        && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                        && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                        && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
                    if (!mismo)
                        return Results.Conflict(new { ok = false, message = "La operación ya fue cobrada con datos distintos. Se bloqueó un posible doble cobro entre computadoras.", operationKey, ventaId = opVentaId });
                }
            }

            if (opVentaId > 0)
            {
                await EnsureLedgerForExistingSaleAsync(con, tx, opVentaId, venta, syncKey, operationKey);
                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = opVentaId, syncKey, operationKey, duplicated = false, idempotent = true, sameOperation = true });
            }
        }

        // V44: respaldo para versiones antiguas sin OperationKey.
        // No se usa para clientes nuevos, porque OperationKey es una identidad más fuerte.
        if (!string.IsNullOrWhiteSpace(legacyFingerprint))
        {
            long huellaVentaId = 0;
            await using (var fpCmd = new MySqlCommand("SELECT id FROM ventas WHERE legacy_fingerprint = @fp LIMIT 1;", con, tx))
            {
                fpCmd.Parameters.AddWithValue("@fp", legacyFingerprint);
                var existingFp = await fpCmd.ExecuteScalarAsync();
                if (existingFp != null) huellaVentaId = Convert.ToInt64(existingFp);
            }

            if (huellaVentaId > 0)
            {
                await EnsureLedgerForExistingSaleAsync(con, tx, huellaVentaId, venta, syncKey, operationKey);
                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = huellaVentaId, syncKey, operationKey, legacyFingerprint, idempotent = true, sameLegacyFingerprint = true });
            }
        }

        if (venta.Total <= 0 || venta.Efectivo < 0 || venta.Qr < 0)
            return Results.BadRequest(new { ok = false, message = "Los importes no pueden ser negativos." });

        string metodoSeguro = (venta.MetodoPago ?? "").Trim().ToUpperInvariant();
        decimal sumaPago = Math.Round(Math.Max(0, venta.Efectivo) + Math.Max(0, venta.Qr), 2);
        decimal totalSeguro = Math.Round(venta.Total, 2);
        bool pagoCuadra = metodoSeguro switch
        {
            "EFECTIVO" => Math.Abs(Math.Max(0, venta.Efectivo) - totalSeguro) < 0.01m && Math.Abs(venta.Qr) < 0.01m,
            "QR" => Math.Abs(Math.Max(0, venta.Qr) - totalSeguro) < 0.01m && Math.Abs(venta.Efectivo) < 0.01m,
            "MIXTO" => Math.Abs(sumaPago - totalSeguro) < 0.01m,
            _ => false
        };
        if (!pagoCuadra)
            return Results.BadRequest(new { ok = false, message = "El método de pago no cuadra con el total. Se bloqueó el registro para evitar descuadres.", total = totalSeguro, efectivo = venta.Efectivo, qr = venta.Qr, metodo = metodoSeguro });

        // V43: valida el contenido económico antes de tocar detalle o stock.
        // DIRECTA y CONSUMO_MESA deben cuadrar con la suma de productos redondeada hacia arriba a Bs. 0,50.
        // MESA puede incluir además el tiempo, por eso los productos nunca pueden superar el total cobrado.
        if (venta.Detalle == null)
            return Results.BadRequest(new { ok = false, message = "El detalle de la venta es obligatorio." });
        if (venta.Detalle.Any(d => d.Cantidad <= 0 || d.PrecioUnitario < 0 || d.Subtotal < 0))
            return Results.BadRequest(new { ok = false, message = "El detalle contiene cantidades o importes inválidos. Se bloqueó la venta." });

        foreach (var d in venta.Detalle)
        {
            decimal esperadoLinea = Math.Round(d.Cantidad * d.PrecioUnitario, 2);
            if (Math.Abs(esperadoLinea - Math.Round(d.Subtotal, 2)) > 0.02m)
                return Results.BadRequest(new { ok = false, message = "Un producto no cuadra con cantidad x precio. Se bloqueó la venta.", producto = d.Producto, cantidad = d.Cantidad, precio = d.PrecioUnitario, subtotal = d.Subtotal, esperado = esperadoLinea });
        }

        decimal detalleTotal = Math.Round(venta.Detalle.Sum(d => d.Subtotal), 2);
        decimal detalleRedondeado = Math.Ceiling(detalleTotal * 2m) / 2m;
        string tipoSeguro = (venta.Tipo ?? "").Trim().ToUpperInvariant();
        // V48: todo cobro ligado a una mesa debe identificar la sesión real.
        // CONSUMO_MESA admite varios pagos parciales; MESA admite un único cierre final.
        if ((tipoSeguro == "MESA" || tipoSeguro == "CONSUMO_MESA") && (!venta.SessionId.HasValue || venta.SessionId.Value <= 0))
            return Results.BadRequest(new { ok = false, message = "El cobro de mesa llegó sin SessionId. Se bloqueó para evitar mezclar turnos, mesas o pagos parciales." });

        if ((tipoSeguro == "DIRECTA" || tipoSeguro == "CONSUMO_MESA") && Math.Abs(detalleRedondeado - totalSeguro) > 0.01m)
            return Results.BadRequest(new { ok = false, message = "El total cobrado no cuadra con los productos. Se bloqueó para evitar desviaciones.", detalle = detalleTotal, esperado = detalleRedondeado, recibido = totalSeguro });
        if (tipoSeguro == "MESA" && detalleTotal - totalSeguro > 0.01m)
            return Results.BadRequest(new { ok = false, message = "Los productos superan el total cobrado de la mesa. Se bloqueó para evitar un descuadre.", detalle = detalleTotal, total = totalSeguro });

        // V48: un cierre final de sesión se contabiliza una sola vez, aunque llegue con otra OperationKey.
        if (tipoSeguro == "MESA" && venta.SessionId.HasValue)
        {
            await using var finalCmd = new MySqlCommand("""
                SELECT id, sync_key, COALESCE(operation_key,'')
                FROM ventas
                WHERE sucursal_id = @sucursal_id AND session_id = @session_id AND UPPER(TRIM(tipo)) = 'MESA'
                ORDER BY id LIMIT 1;
            """, con, tx);
            finalCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            finalCmd.Parameters.AddWithValue("@session_id", venta.SessionId.Value);
            await using var finalRd = await finalCmd.ExecuteReaderAsync();
            if (await finalRd.ReadAsync())
            {
                long existingFinalId = finalRd.GetInt64(0);
                string existingSync = finalRd.IsDBNull(1) ? "" : finalRd.GetString(1);
                string existingOp = finalRd.IsDBNull(2) ? "" : finalRd.GetString(2);
                if (string.Equals(existingSync, syncKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existingOp, operationKey, StringComparison.OrdinalIgnoreCase))
                {
                    await finalRd.DisposeAsync();
                    await tx.CommitAsync();
                    return Results.Ok(new { ok = true, id = existingFinalId, syncKey, operationKey, idempotent = true, sameSessionFinal = true });
                }
                return Results.Conflict(new { ok = false, message = "Esta sesión de mesa ya tiene un cobro final confirmado. Se bloqueó un segundo cierre.", sessionId = venta.SessionId, ventaId = existingFinalId });
            }
        }

        // V48: una línea de consumo identificada no puede aparecer en dos ventas distintas.
        // Esto evita que un producto pagado parcialmente reaparezca al cierre por una copia online atrasada.
        foreach (var d in venta.Detalle)
        {
            string consumptionKey = (d.ConsumptionKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(consumptionKey)) continue;
            await using var ckCmd = new MySqlCommand("SELECT venta_id FROM detalle_ventas WHERE consumption_key = @ck LIMIT 1;", con, tx);
            ckCmd.Parameters.AddWithValue("@ck", consumptionKey);
            object? existingCk = await ckCmd.ExecuteScalarAsync();
            if (existingCk != null)
                return Results.Conflict(new { ok = false, message = "Este producto de mesa ya fue cobrado anteriormente. Se bloqueó para evitar doble cobro.", consumptionKey, ventaId = Convert.ToInt64(existingCk) });
        }

        bool ventaYaExistia = false;

        const string ventaSql = """
            INSERT IGNORE INTO ventas (sucursal_id, cajero, caja_nombre, turno, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, legacy_fingerprint, session_id)
            VALUES (@sucursal_id, @cajero, @caja_nombre, @turno, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), NULLIF(@legacy_fingerprint,''), @session_id);
        """;

        await using var ventaCmd = new MySqlCommand(ventaSql, con, tx);
        ventaCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
        ventaCmd.Parameters.AddWithValue("@cajero", venta.Cajero);
        ventaCmd.Parameters.AddWithValue("@caja_nombre", string.IsNullOrWhiteSpace(venta.CajaNombre) ? (object)DBNull.Value : venta.CajaNombre.Trim());
        ventaCmd.Parameters.AddWithValue("@turno", string.IsNullOrWhiteSpace(venta.Turno) ? HoraATurno(venta.Fecha) : NormalizarTurno(venta.Turno));
        ventaCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
        ventaCmd.Parameters.AddWithValue("@tipo", venta.Tipo);
        ventaCmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago);
        ventaCmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
        ventaCmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
        ventaCmd.Parameters.AddWithValue("@total", venta.Total);
        ventaCmd.Parameters.AddWithValue("@sync_key", syncKey);
        ventaCmd.Parameters.AddWithValue("@operation_key", operationKey);
        ventaCmd.Parameters.AddWithValue("@legacy_fingerprint", legacyFingerprint);
        ventaCmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);

        int insertedRows = await ventaCmd.ExecuteNonQueryAsync();
        long ventaId;
        await using (var idCmd = new MySqlCommand("""
            SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
            FROM ventas
            WHERE sync_key = @sync_key
               OR (@operation_key <> '' AND operation_key = @operation_key)
               OR (@legacy_fingerprint <> '' AND legacy_fingerprint = @legacy_fingerprint)
            ORDER BY CASE
                WHEN sync_key = @sync_key THEN 0
                WHEN @operation_key <> '' AND operation_key = @operation_key THEN 1
                ELSE 2
            END
            LIMIT 1;
        """, con, tx))
        {
            idCmd.Parameters.AddWithValue("@sync_key", syncKey);
            idCmd.Parameters.AddWithValue("@operation_key", operationKey);
            idCmd.Parameters.AddWithValue("@legacy_fingerprint", legacyFingerprint);
            await using var rd = await idCmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
                return Results.Conflict(new { ok = false, message = "No se pudo asegurar la identidad única del cobro. No se modificó inventario.", syncKey, operationKey });

            ventaId = rd.GetInt64(0);
            bool mismo = rd.GetInt32(1) == venta.SucursalId
                && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "Se detectó una colisión de identidad con otro cobro. Operación bloqueada.", syncKey, operationKey, ventaId });
        }

        ventaYaExistia = insertedRows == 0;
        if (ventaYaExistia)
        {
            await tx.CommitAsync();
            return Results.Ok(new { ok = true, id = ventaId, syncKey, operationKey, idempotent = true });
        }

        await using (var del = new MySqlCommand("DELETE FROM detalle_ventas WHERE venta_id = @venta_id;", con, tx))
        {
            del.Parameters.AddWithValue("@venta_id", ventaId);
            await del.ExecuteNonQueryAsync();
        }

        for (int detalleIndex = 0; detalleIndex < venta.Detalle.Count; detalleIndex++)
        {
            var d = venta.Detalle[detalleIndex];
            string consumptionKey = (d.ConsumptionKey ?? "").Trim();
            string lineKey = BuildSaleLineKey(operationKey, syncKey, detalleIndex, d);
            string nombreProducto = string.IsNullOrWhiteSpace(d.Producto) ? "Producto" : d.Producto.Trim();
            string nombrePresentacion = string.IsNullOrWhiteSpace(d.Presentacion) ? "Unidad" : d.Presentacion.Trim();
            decimal cantidadBase = d.CantidadBase <= 0 ? d.Cantidad : d.CantidadBase;

            // Los ID de SQLite/JSON de la PC no se reutilizan como ID de MySQL.
            // Railway resuelve producto por sucursal + nombre y presentación por producto + nombre.
            // Así no se crean duplicados ni se pisa otro producto cuando los ID locales difieren.
            long productoId;
            await using (var findProd = new MySqlCommand("""
                SELECT id
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre))
                ORDER BY id
                LIMIT 1;
            """, con, tx))
            {
                findProd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                findProd.Parameters.AddWithValue("@nombre", nombreProducto);
                var found = await findProd.ExecuteScalarAsync();
                if (found != null)
                {
                    productoId = Convert.ToInt64(found);
                    await using var activar = new MySqlCommand("UPDATE productos SET estado = 'ACTIVO' WHERE id = @id;", con, tx);
                    activar.Parameters.AddWithValue("@id", productoId);
                    await activar.ExecuteNonQueryAsync();
                }
                else
                {
                    string categoriaNueva = NormalizarCategoriaProducto(null, nombreProducto);
                    await using var insertProd = new MySqlCommand("""
                        INSERT INTO productos
                        (sucursal_id, nombre, categoria, tipo_entrada, unidad_base, unidades_por_entrada,
                         precio_compra, stock_actual, stock_minimo, estado)
                        VALUES
                        (@sucursal_id, @nombre, @categoria, 'UNIDAD', 'UNIDAD', 1, 0, 0, 0, 'ACTIVO');
                        SELECT LAST_INSERT_ID();
                    """, con, tx);
                    insertProd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                    insertProd.Parameters.AddWithValue("@nombre", nombreProducto);
                    insertProd.Parameters.AddWithValue("@categoria", categoriaNueva);
                    productoId = Convert.ToInt64(await insertProd.ExecuteScalarAsync());
                }
            }

            long presentacionId;
            await using (var findPres = new MySqlCommand("""
                SELECT id
                FROM presentaciones
                WHERE producto_id = @producto_id
                  AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre))
                ORDER BY id
                LIMIT 1;
            """, con, tx))
            {
                findPres.Parameters.AddWithValue("@producto_id", productoId);
                findPres.Parameters.AddWithValue("@nombre", nombrePresentacion);
                var found = await findPres.ExecuteScalarAsync();
                if (found != null)
                {
                    presentacionId = Convert.ToInt64(found);
                    await using var updatePres = new MySqlCommand("""
                        UPDATE presentaciones
                        SET cantidad_base = @cantidad_base,
                            precio_venta = @precio_venta,
                            estado = 'ACTIVO'
                        WHERE id = @id;
                    """, con, tx);
                    updatePres.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    updatePres.Parameters.AddWithValue("@precio_venta", d.PrecioUnitario);
                    updatePres.Parameters.AddWithValue("@id", presentacionId);
                    await updatePres.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var insertPres = new MySqlCommand("""
                        INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
                        VALUES (@producto_id, @nombre, @cantidad_base, @precio_venta, 'ACTIVO');
                        SELECT LAST_INSERT_ID();
                    """, con, tx);
                    insertPres.Parameters.AddWithValue("@producto_id", productoId);
                    insertPres.Parameters.AddWithValue("@nombre", nombrePresentacion);
                    insertPres.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    insertPres.Parameters.AddWithValue("@precio_venta", d.PrecioUnitario);
                    presentacionId = Convert.ToInt64(await insertPres.ExecuteScalarAsync());
                }
            }

            const string detalleSql = """
                INSERT IGNORE INTO detalle_ventas
                (venta_id, producto_id, presentacion_id, producto, presentacion, cantidad, precio_unitario, subtotal, line_key, consumption_key)
                VALUES
                (@venta_id, @producto_id, @presentacion_id, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal, @line_key, NULLIF(@consumption_key,''));
            """;

            await using var detCmd = new MySqlCommand(detalleSql, con, tx);
            detCmd.Parameters.AddWithValue("@venta_id", ventaId);
            detCmd.Parameters.AddWithValue("@producto_id", productoId);
            detCmd.Parameters.AddWithValue("@presentacion_id", presentacionId);
            detCmd.Parameters.AddWithValue("@producto", d.Producto);
            detCmd.Parameters.AddWithValue("@presentacion", d.Presentacion);
            detCmd.Parameters.AddWithValue("@cantidad", d.Cantidad);
            detCmd.Parameters.AddWithValue("@precio_unitario", d.PrecioUnitario);
            detCmd.Parameters.AddWithValue("@subtotal", d.Subtotal);
            detCmd.Parameters.AddWithValue("@line_key", lineKey);
            detCmd.Parameters.AddWithValue("@consumption_key", consumptionKey);
            int detailInserted = await detCmd.ExecuteNonQueryAsync();

            // V47: el inventario tiene su propio libro idempotente. Aun si por un error de red o
            // programación se intenta procesar otra vez la misma línea, movement_key solo puede
            // existir una vez y el stock NO se vuelve a descontar.
            if (!ventaYaExistia && detailInserted > 0 && !d.StockAlreadyDiscountedOnline)
            {
                int movementInserted;
                await using (var movementCmd = new MySqlCommand("""
                    INSERT IGNORE INTO stock_movimientos_venta
                    (movement_key, venta_id, sucursal_id, producto_id, presentacion_id, cantidad_base, fecha)
                    VALUES
                    (@movement_key, @venta_id, @sucursal_id, @producto_id, @presentacion_id, @cantidad_base, @fecha);
                """, con, tx))
                {
                    movementCmd.Parameters.AddWithValue("@movement_key", string.IsNullOrWhiteSpace(consumptionKey) ? lineKey : consumptionKey);
                    movementCmd.Parameters.AddWithValue("@venta_id", ventaId);
                    movementCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                    movementCmd.Parameters.AddWithValue("@producto_id", productoId);
                    movementCmd.Parameters.AddWithValue("@presentacion_id", presentacionId);
                    movementCmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    movementCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
                    movementInserted = await movementCmd.ExecuteNonQueryAsync();
                }

                if (movementInserted > 0)
                {
                    await using var stockCmd = new MySqlCommand("""
                        UPDATE productos
                        SET stock_actual = CASE
                            WHEN COALESCE(sin_limite_stock, 0) = 1 OR categoria = 'Servidos en vaso'
                            THEN stock_actual
                            ELSE GREATEST(stock_actual - @cantidad_base, 0)
                        END
                        WHERE id = @producto_id;
                    """, con, tx);
                    stockCmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    stockCmd.Parameters.AddWithValue("@producto_id", productoId);
                    await stockCmd.ExecuteNonQueryAsync();
                }
            }
        }

        // V45: libro contable inmutable dentro de la MISMA transacción que venta+detalle+stock.
        // Si este registro falla, se revierte toda la operación y no queda un cobro a medias.
        await using (var ledgerCmd = new MySqlCommand("""
            INSERT INTO libro_caja
            (venta_id, sucursal_id, cajero, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, session_id, estado)
            VALUES
            (@venta_id, @sucursal_id, @cajero, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), @session_id, 'CONFIRMADA');
        """, con, tx))
        {
            ledgerCmd.Parameters.AddWithValue("@venta_id", ventaId);
            ledgerCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            ledgerCmd.Parameters.AddWithValue("@cajero", venta.Cajero ?? "");
            ledgerCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
            ledgerCmd.Parameters.AddWithValue("@tipo", venta.Tipo ?? "VENTA");
            ledgerCmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago ?? "");
            ledgerCmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
            ledgerCmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
            ledgerCmd.Parameters.AddWithValue("@total", venta.Total);
            ledgerCmd.Parameters.AddWithValue("@sync_key", syncKey);
            ledgerCmd.Parameters.AddWithValue("@operation_key", operationKey);
            ledgerCmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();
        }

        await using (var auditCmd = new MySqlCommand("""
            INSERT INTO auditoria_contable
            (fecha, usuario, sucursal_id, accion, entidad, entidad_id, detalle)
            VALUES (NOW(), @usuario, @sucursal_id, 'CONFIRMAR_COBRO', 'VENTA', @entidad_id, @detalle);
        """, con, tx))
        {
            auditCmd.Parameters.AddWithValue("@usuario", venta.Cajero ?? "");
            auditCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            auditCmd.Parameters.AddWithValue("@entidad_id", ventaId);
            auditCmd.Parameters.AddWithValue("@detalle", $"{venta.Tipo}|{venta.MetodoPago}|{venta.Total:0.00}|{syncKey}|{operationKey}");
            await auditCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        await TrySyncSheets(db, sheets);

        return Results.Ok(new { ok = true, id = ventaId, syncKey, operationKey });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("Error al guardar venta: " + ex.Message);
    }
});

app.MapGet("/api/ventas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureVentaSyncProtection(con);
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN efectivo DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN qr DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }

    const string sql = """
        SELECT v.id, v.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, v.cajero,
               COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), CASE WHEN v.sucursal_id=1 THEN 'CAJA ÚNICA' ELSE 'SIN CAJA' END) AS caja_nombre,
               COALESCE(NULLIF(v.turno,''), NULLIF(u.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END) AS turno,
               v.fecha, v.tipo, v.metodo_pago, COALESCE(v.efectivo,0) AS efectivo, COALESCE(v.qr,0) AS qr, v.total, v.sync_key, COALESCE(v.operation_key,'') AS operation_key, v.session_id
        FROM ventas v
        INNER JOIN sucursales s ON s.id = v.sucursal_id
        LEFT JOIN usuarios u ON u.usuario=v.cajero AND u.sucursal_id=v.sucursal_id
        WHERE (@sucursalId IS NULL OR v.sucursal_id = @sucursalId)
        ORDER BY v.fecha DESC, v.id DESC
        LIMIT 10000;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});



app.MapGet("/api/detalle-ventas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();

    string where = sucursalId.HasValue ? "WHERE v.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT d.venta_id AS id_venta,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               v.cajero,
               d.producto,
               d.presentacion,
               d.cantidad,
               d.precio_unitario AS precio,
               d.subtotal,
               COALESCE(d.consumption_key,'') AS consumption_key
        FROM detalle_ventas d
        INNER JOIN ventas v ON v.id = d.venta_id
        INNER JOIN sucursales s ON s.id = v.sucursal_id
        {where}
        ORDER BY d.venta_id DESC, d.id DESC;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapGet("/api/admin/conciliacion", async (Db db, string? clave, int? sucursalId) =>
{
    if (clave != "ENTREGAR_LIMPIO_2026") return Results.Unauthorized();
    await using var con = await db.OpenAsync();
    await EnsureAccountingLedger(con);

    const string sql = """
        SELECT
            COALESCE((SELECT SUM(total) FROM ventas WHERE (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS ventas_total,
            COALESCE((SELECT SUM(total) FROM libro_caja WHERE estado='CONFIRMADA' AND (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS libro_total,
            COALESCE((SELECT SUM(efectivo+qr) FROM libro_caja WHERE estado='CONFIRMADA' AND (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS medios_total,
            COALESCE((SELECT COUNT(*) FROM ventas WHERE (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS ventas_count,
            COALESCE((SELECT COUNT(*) FROM libro_caja WHERE estado='CONFIRMADA' AND (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS libro_count;
    """;
    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursalId", sucursalId.HasValue ? sucursalId.Value : DBNull.Value);
    await using var rd = await cmd.ExecuteReaderAsync();
    await rd.ReadAsync();
    decimal ventasTotal = rd.GetDecimal(0);
    decimal libroTotal = rd.GetDecimal(1);
    decimal mediosTotal = rd.GetDecimal(2);
    long ventasCount = rd.GetInt64(3);
    long libroCount = rd.GetInt64(4);
    bool cuadra = Math.Abs(ventasTotal-libroTotal) < 0.01m && Math.Abs(libroTotal-mediosTotal) < 0.01m && ventasCount == libroCount;
    return Results.Ok(new { ok=true, cuadra, ventasTotal, libroTotal, mediosTotal, ventasCount, libroCount, diferenciaVentasLibro = ventasTotal-libroTotal, diferenciaLibroMedios = libroTotal-mediosTotal });
});

app.MapPost("/api/cobros-mesa", async (Db db, SheetsReporter sheets, CobroMesaRequest c) =>
{
    await using var con = await db.OpenAsync();

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cobros_mesa (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            session_id INT NULL,
            mesa_id INT NULL,
            mesa VARCHAR(100) NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            mesera VARCHAR(150) NULL,
            fecha DATETIME NOT NULL,
            tiempo VARCHAR(50) NULL,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_cobrado DECIMAL(10,2) NOT NULL DEFAULT 0,
            metodo_pago VARCHAR(50) NOT NULL,
            sync_key VARCHAR(180) NOT NULL UNIQUE,
            UNIQUE KEY uk_cobro_sesion (sucursal_id, session_id)
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    // V49: una sesión solo puede tener un cobro de mesa confirmado.
    try { await new MySqlCommand("ALTER TABLE cobros_mesa ADD UNIQUE KEY uk_cobro_sesion (sucursal_id, session_id);", con).ExecuteNonQueryAsync(); } catch { }

    // V37: el detalle de tiempo ahora incluye modalidad, tiempo real, horas cobradas y tarifa.
    try
    {
        await using var widen = new MySqlCommand("ALTER TABLE cobros_mesa MODIFY COLUMN tiempo VARCHAR(220) NULL;", con);
        await widen.ExecuteNonQueryAsync();
    }
    catch { }

    string syncKey = string.IsNullOrWhiteSpace(c.SyncKey) ? Guid.NewGuid().ToString("N") : c.SyncKey;

    // V49: aunque otra PC o un reintento cambie sync_key, la misma SessionId no puede cobrarse dos veces.
    if (c.SessionId.HasValue && c.SessionId.Value > 0)
    {
        await using var sameSession = new MySqlCommand("""
            SELECT id, COALESCE(mesa_id,0), total_mesa, total_consumo, total_cobrado, metodo_pago, sync_key
            FROM cobros_mesa
            WHERE sucursal_id=@sucursal_id AND session_id=@session_id
            ORDER BY id LIMIT 1;
        """, con);
        sameSession.Parameters.AddWithValue("@sucursal_id", c.SucursalId);
        sameSession.Parameters.AddWithValue("@session_id", c.SessionId.Value);
        await using var rdSession = await sameSession.ExecuteReaderAsync();
        if (await rdSession.ReadAsync())
        {
            long idExistente = rdSession.GetInt64(0);
            bool mismo = rdSession.GetInt32(1) == (c.MesaId ?? 0)
                && Math.Abs(rdSession.GetDecimal(2) - c.TotalMesa) < 0.01m
                && Math.Abs(rdSession.GetDecimal(3) - c.TotalConsumo) < 0.01m
                && Math.Abs(rdSession.GetDecimal(4) - c.TotalCobrado) < 0.01m
                && string.Equals(rdSession.GetString(5), c.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase);
            string oldSync = rdSession.IsDBNull(6) ? "" : rdSession.GetString(6);
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "Esta sesión ya fue cobrada con datos distintos. Se bloqueó para evitar doble cobro o alteración.", sessionId = c.SessionId, id = idExistente });
            return Results.Ok(new { ok = true, syncKey = oldSync, id = idExistente, idempotent = true, sameSession = true });
        }
    }

    // V42: cobro de mesa idempotente e inmutable por sync_key.
    await using (var existing = new MySqlCommand("""
        SELECT id, sucursal_id, COALESCE(session_id,0), COALESCE(mesa_id,0), total_mesa, total_consumo, total_cobrado, metodo_pago
        FROM cobros_mesa WHERE sync_key = @sync_key LIMIT 1;
    """, con))
    {
        existing.Parameters.AddWithValue("@sync_key", syncKey);
        await using var rd = await existing.ExecuteReaderAsync();
        if (await rd.ReadAsync())
        {
            bool mismo = rd.GetInt32(1) == c.SucursalId
                && rd.GetInt32(2) == (c.SessionId ?? 0)
                && rd.GetInt32(3) == (c.MesaId ?? 0)
                && Math.Abs(rd.GetDecimal(4) - c.TotalMesa) < 0.01m
                && Math.Abs(rd.GetDecimal(5) - c.TotalConsumo) < 0.01m
                && Math.Abs(rd.GetDecimal(6) - c.TotalCobrado) < 0.01m
                && string.Equals(rd.GetString(7), c.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase);
            long idExistente = rd.GetInt64(0);
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "La misma sync_key de cobro ya existe con datos distintos. Se bloqueó para evitar doble cobro.", syncKey, id = idExistente });
            return Results.Ok(new { ok = true, syncKey, id = idExistente, idempotent = true });
        }
    }

    if (c.TotalMesa < 0 || c.TotalConsumo < 0 || c.TotalCobrado < 0)
        return Results.BadRequest(new { ok = false, message = "Los totales del cobro no pueden ser negativos." });

    decimal esperadoCobro = Math.Round(c.TotalMesa + c.TotalConsumo, 2);
    if (Math.Abs(Math.Round(c.TotalCobrado, 2) - esperadoCobro) > 0.51m)
        return Results.BadRequest(new { ok = false, message = "El total cobrado no coincide con mesa + consumo. Se bloqueó para evitar descuadre.", esperado = esperadoCobro, recibido = c.TotalCobrado });

    const string sql = """
        INSERT INTO cobros_mesa
        (sucursal_id, session_id, mesa_id, mesa, cajero, mesera, fecha, tiempo, total_mesa, total_consumo, total_cobrado, metodo_pago, sync_key)
        VALUES
        (@sucursal_id, @session_id, @mesa_id, @mesa, @cajero, @mesera, @fecha, @tiempo, @total_mesa, @total_consumo, @total_cobrado, @metodo_pago, @sync_key)
        ON DUPLICATE KEY UPDATE
            sync_key = VALUES(sync_key);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", c.SucursalId);
    cmd.Parameters.AddWithValue("@session_id", c.SessionId.HasValue ? c.SessionId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesa_id", c.MesaId.HasValue ? c.MesaId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesa", c.Mesa ?? "");
    cmd.Parameters.AddWithValue("@cajero", c.Cajero ?? "");
    cmd.Parameters.AddWithValue("@mesera", c.Mesera ?? "");
    cmd.Parameters.AddWithValue("@fecha", c.Fecha);
    cmd.Parameters.AddWithValue("@tiempo", c.Tiempo ?? "");
    cmd.Parameters.AddWithValue("@total_mesa", c.TotalMesa);
    cmd.Parameters.AddWithValue("@total_consumo", c.TotalConsumo);
    cmd.Parameters.AddWithValue("@total_cobrado", c.TotalCobrado);
    cmd.Parameters.AddWithValue("@metodo_pago", c.MetodoPago ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});


app.MapGet("/api/cobros-mesa", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cobros_mesa (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            session_id INT NULL,
            mesa_id INT NULL,
            mesa VARCHAR(100) NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            mesera VARCHAR(150) NULL,
            fecha DATETIME NOT NULL,
            tiempo VARCHAR(50) NULL,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_cobrado DECIMAL(10,2) NOT NULL DEFAULT 0,
            metodo_pago VARCHAR(50) NOT NULL,
            sync_key VARCHAR(180) NOT NULL UNIQUE,
            UNIQUE KEY uk_cobro_sesion (sucursal_id, session_id)
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    try
    {
        await using var widen = new MySqlCommand("ALTER TABLE cobros_mesa MODIFY COLUMN tiempo VARCHAR(220) NULL;", con);
        await widen.ExecuteNonQueryAsync();
    }
    catch { }

    string where = sucursalId.HasValue ? "WHERE c.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT c.id, c.session_id, DATE(c.fecha) AS fecha, TIME(c.fecha) AS hora,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               c.mesa, c.cajero, c.mesera, c.tiempo,
               c.total_mesa, c.total_consumo, c.total_cobrado, c.metodo_pago
        FROM cobros_mesa c
        INNER JOIN sucursales s ON s.id = c.sucursal_id
        {where}
        ORDER BY c.fecha DESC, c.id DESC;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});


app.MapPost("/api/mesas/estado", async (Db db, SheetsReporter sheets, MesaEstadoRequest m) =>
{
    await using var con = await db.OpenAsync();

    await EnsureMesasEnVivoTables(con);

    string syncKey = string.IsNullOrWhiteSpace(m.SyncKey)
        ? $"MESA-{m.SucursalId}-{m.MesaId}"
        : m.SyncKey;

    const string sql = """
        INSERT INTO mesa_estados
        (sucursal_id, mesa_id, mesa, estado, cajero, inicio, fin_programado, minutos, tarifa_hora, total_mesa, total_consumo, total_general, cliente_reserva, actualizado, sync_key)
        VALUES
        (@sucursal_id, @mesa_id, @mesa, @estado, @cajero, @inicio, @fin_programado, @minutos, @tarifa_hora, @total_mesa, @total_consumo, @total_general, @cliente_reserva, NOW(), @sync_key)
        ON DUPLICATE KEY UPDATE
            mesa = VALUES(mesa),
            estado = VALUES(estado),
            cajero = VALUES(cajero),
            inicio = VALUES(inicio),
            fin_programado = VALUES(fin_programado),
            minutos = VALUES(minutos),
            tarifa_hora = VALUES(tarifa_hora),
            total_mesa = VALUES(total_mesa),
            total_consumo = VALUES(total_consumo),
            total_general = VALUES(total_general),
            cliente_reserva = VALUES(cliente_reserva),
            actualizado = NOW();
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
    cmd.Parameters.AddWithValue("@mesa_id", m.MesaId);
    cmd.Parameters.AddWithValue("@mesa", m.Mesa ?? ("Mesa " + m.MesaId));
    cmd.Parameters.AddWithValue("@estado", m.Estado ?? "LIBRE");
    cmd.Parameters.AddWithValue("@cajero", m.Cajero ?? "");
    cmd.Parameters.AddWithValue("@inicio", m.Inicio.HasValue ? m.Inicio.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@fin_programado", m.FinProgramado.HasValue ? m.FinProgramado.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@minutos", m.Minutos);
    decimal tarifaVigente = m.TarifaHora > 0 ? m.TarifaHora : await EnsureGlobalTableRateAsync(con);
    cmd.Parameters.AddWithValue("@tarifa_hora", tarifaVigente);
    cmd.Parameters.AddWithValue("@total_mesa", m.TotalMesa);
    cmd.Parameters.AddWithValue("@total_consumo", m.TotalConsumo);
    cmd.Parameters.AddWithValue("@total_general", m.TotalGeneral);
    cmd.Parameters.AddWithValue("@cliente_reserva", m.ClienteReserva ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);
    await cmd.ExecuteNonQueryAsync();

    await using (var del = new MySqlCommand("DELETE FROM mesa_consumos_vivos WHERE sucursal_id = @sucursal_id AND mesa_id = @mesa_id;", con))
    {
        del.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
        del.Parameters.AddWithValue("@mesa_id", m.MesaId);
        await del.ExecuteNonQueryAsync();
    }

    foreach (var d in m.Detalle ?? new List<MesaConsumoVivoRequest>())
    {
        await using var det = new MySqlCommand("""
            INSERT INTO mesa_consumos_vivos
            (sucursal_id, mesa_id, producto, presentacion, cantidad, precio_unitario, subtotal, mobile_order_id, stock_already_discounted_online, consumption_key, actualizado)
            VALUES
            (@sucursal_id, @mesa_id, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal, @mobile_order_id, @stock_already_discounted_online, NULLIF(@consumption_key,''), NOW());
        """, con);
        det.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
        det.Parameters.AddWithValue("@mesa_id", m.MesaId);
        det.Parameters.AddWithValue("@producto", d.Producto ?? "");
        det.Parameters.AddWithValue("@presentacion", d.Presentacion ?? "");
        det.Parameters.AddWithValue("@cantidad", d.Cantidad);
        det.Parameters.AddWithValue("@precio_unitario", d.PrecioUnitario);
        det.Parameters.AddWithValue("@subtotal", d.Subtotal);
        det.Parameters.AddWithValue("@mobile_order_id", Math.Max(0, d.MobileOrderId));
        det.Parameters.AddWithValue("@stock_already_discounted_online", d.StockAlreadyDiscountedOnline ? 1 : 0);
        det.Parameters.AddWithValue("@consumption_key", (d.ConsumptionKey ?? "").Trim());
        await det.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { ok = true, syncKey });
});

app.MapGet("/api/mesas/estado", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);

    string where = sucursalId.HasValue ? "WHERE e.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT e.sucursal_id,
               CASE WHEN e.sucursal_id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               e.mesa_id, e.mesa, e.estado, e.cajero, e.inicio, e.fin_programado,
               e.minutos, e.tarifa_hora, e.total_mesa, e.total_consumo, e.total_general,
               e.cliente_reserva, e.actualizado
        FROM mesa_estados e
        {where}
        ORDER BY e.sucursal_id, e.mesa_id;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapGet("/api/mesas/consumos-vivos", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);

    string where = sucursalId.HasValue ? "WHERE c.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT c.sucursal_id,
               CASE WHEN c.sucursal_id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               c.mesa_id, c.producto, c.presentacion, c.cantidad, c.precio_unitario, c.subtotal,
               c.mobile_order_id, c.stock_already_discounted_online, COALESCE(c.consumption_key,'') AS consumption_key
        FROM mesa_consumos_vivos c
        {where}
        ORDER BY c.sucursal_id, c.mesa_id, c.id;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapPost("/api/reservas", async (Db db, SheetsReporter sheets, ReservaRequest r) =>
{
    await using var con = await db.OpenAsync();
    string syncKey = string.IsNullOrWhiteSpace(r.SyncKey) ? Guid.NewGuid().ToString("N") : r.SyncKey;

    const string sql = """
        INSERT INTO reservas
        (sucursal_id, mesa_id, cliente, celular, fecha_reserva, minutos, estado, cajero, sync_key)
        VALUES
        (@sucursal_id, @mesa_id, @cliente, @celular, @fecha_reserva, @minutos, @estado, @cajero, @sync_key)
        ON DUPLICATE KEY UPDATE
            cliente = VALUES(cliente),
            celular = VALUES(celular),
            fecha_reserva = VALUES(fecha_reserva),
            minutos = VALUES(minutos),
            estado = VALUES(estado);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
    cmd.Parameters.AddWithValue("@mesa_id", r.MesaId);
    cmd.Parameters.AddWithValue("@cliente", r.Cliente);
    cmd.Parameters.AddWithValue("@celular", r.Celular ?? "");
    cmd.Parameters.AddWithValue("@fecha_reserva", r.FechaReserva);
    cmd.Parameters.AddWithValue("@minutos", r.Minutos);
    cmd.Parameters.AddWithValue("@estado", r.Estado);
    cmd.Parameters.AddWithValue("@cajero", r.Cajero ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});

app.MapPost("/api/propinas", async (Db db, SheetsReporter sheets, PropinaRequest p) =>
{
    await using var con = await db.OpenAsync();
    string syncKey = string.IsNullOrWhiteSpace(p.SyncKey) ? Guid.NewGuid().ToString("N") : p.SyncKey;

    const string sql = """
        INSERT INTO propinas
        (sucursal_id, mesa_id, mesera, cajero, fecha, monto, sync_key)
        VALUES
        (@sucursal_id, @mesa_id, @mesera, @cajero, @fecha, @monto, @sync_key)
        ON DUPLICATE KEY UPDATE
            monto = VALUES(monto);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
    cmd.Parameters.AddWithValue("@mesa_id", p.MesaId.HasValue ? p.MesaId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesera", p.Mesera);
    cmd.Parameters.AddWithValue("@cajero", p.Cajero);
    cmd.Parameters.AddWithValue("@fecha", p.Fecha);
    cmd.Parameters.AddWithValue("@monto", p.Monto);
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});


// V35: cierre de turno/arqueo inmutable.
// La caja envía una fotografía completa del turno; el Administrador la consulta después.
app.MapPost("/api/cierres-turno", async (Db db, SheetsReporter sheets, ShiftCloseRequest r) =>
{
    await using var con = await db.OpenAsync();
    await EnsureShiftCloseTables(con);

    string syncKey = string.IsNullOrWhiteSpace(r.SyncKey)
        ? "CIERRE-" + r.SucursalId + "-" + (r.CajeroUsuario ?? "") + "-" + r.Inicio.Ticks
        : r.SyncKey.Trim();

    // V56: el servidor vuelve a calcular el dinero del arqueo usando el libro canónico de VENTAS.
    // Así un cierre local desactualizado no puede dejar Bs. 0 si Railway ya tiene ventas confirmadas.
    int canonicalTransactions = Math.Max(0, r.TransaccionesTotal);
    int canonicalCashTransactions = Math.Max(0, r.TransaccionesEfectivo);
    int canonicalQrTransactions = Math.Max(0, r.TransaccionesQr);
    decimal canonicalCash = Math.Max(0, r.Efectivo);
    decimal canonicalQr = Math.Max(0, r.Qr);
    decimal canonicalTotal = Math.Max(0, r.TotalGenerado);
    bool reconciledFromSales = false;

    try
    {
        await EnsureVentaSyncProtection(con);
        await using var reconcile = new MySqlCommand("""
            SELECT COUNT(*) AS operaciones,
                   COALESCE(SUM(CASE WHEN efectivo > 0 THEN 1 ELSE 0 END),0) AS ops_efectivo,
                   COALESCE(SUM(CASE WHEN qr > 0 THEN 1 ELSE 0 END),0) AS ops_qr,
                   COALESCE(SUM(efectivo),0) AS efectivo,
                   COALESCE(SUM(qr),0) AS qr,
                   COALESCE(SUM(total),0) AS total
            FROM ventas
            WHERE sucursal_id = @sucursal_id
              AND cajero = @cajero
              AND fecha >= @inicio AND fecha < @fin;
        """, con);
        reconcile.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
        reconcile.Parameters.AddWithValue("@cajero", r.CajeroUsuario ?? "");
        reconcile.Parameters.AddWithValue("@inicio", r.Inicio);
        reconcile.Parameters.AddWithValue("@fin", r.Fin);
        await using var rr = await reconcile.ExecuteReaderAsync();
        if (await rr.ReadAsync())
        {
            int count = Convert.ToInt32(rr["operaciones"] ?? 0);
            if (count > 0)
            {
                canonicalTransactions = count;
                canonicalCashTransactions = Convert.ToInt32(rr["ops_efectivo"] ?? 0);
                canonicalQrTransactions = Convert.ToInt32(rr["ops_qr"] ?? 0);
                canonicalCash = Convert.ToDecimal(rr["efectivo"] ?? 0m);
                canonicalQr = Convert.ToDecimal(rr["qr"] ?? 0m);
                canonicalTotal = Convert.ToDecimal(rr["total"] ?? 0m);
                reconciledFromSales = true;
            }
        }
    }
    catch { /* si aún no llegaron ventas al servidor, se conserva la fotografía enviada por la caja */ }

    const string sql = """
        INSERT INTO cierres_turno
        (
            sucursal_id, sucursal, cajero_usuario, cajero_nombre, caja, turno,
            inicio, fin, hora_entrada, fecha_cierre,
            transacciones_total, transacciones_efectivo, transacciones_qr,
            transacciones_tarjeta, transacciones_transferencia,
            efectivo, qr, tarjeta, transferencia, sin_metodo,
            productos_total, mesas_total, minutos_jugados, propinas_total,
            cortesias_valor, comisiones_total, gastos_total, perdidas_total,
            total_generado, neto_turno, observaciones, detalle_json, sync_key
        )
        VALUES
        (
            @sucursal_id, @sucursal, @cajero_usuario, @cajero_nombre, @caja, @turno,
            @inicio, @fin, @hora_entrada, @fecha_cierre,
            @transacciones_total, @transacciones_efectivo, @transacciones_qr,
            @transacciones_tarjeta, @transacciones_transferencia,
            @efectivo, @qr, @tarjeta, @transferencia, @sin_metodo,
            @productos_total, @mesas_total, @minutos_jugados, @propinas_total,
            @cortesias_valor, @comisiones_total, @gastos_total, @perdidas_total,
            @total_generado, @neto_turno, @observaciones, @detalle_json, @sync_key
        )
        ON DUPLICATE KEY UPDATE sync_key = VALUES(sync_key);
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
        cmd.Parameters.AddWithValue("@sucursal", string.IsNullOrWhiteSpace(r.Sucursal) ? (r.SucursalId == 2 ? "EL BRUJO PREMIU" : "EL BRUJO") : r.Sucursal.Trim());
        cmd.Parameters.AddWithValue("@cajero_usuario", r.CajeroUsuario ?? "");
        cmd.Parameters.AddWithValue("@cajero_nombre", r.CajeroNombre ?? "");
        cmd.Parameters.AddWithValue("@caja", r.Caja ?? "");
        cmd.Parameters.AddWithValue("@turno", NormalizarTurno(r.Turno));
        cmd.Parameters.AddWithValue("@inicio", r.Inicio);
        cmd.Parameters.AddWithValue("@fin", r.Fin);
        cmd.Parameters.AddWithValue("@hora_entrada", r.HoraEntrada == DateTime.MinValue ? r.Inicio : r.HoraEntrada);
        cmd.Parameters.AddWithValue("@fecha_cierre", r.FechaCierre);
        cmd.Parameters.AddWithValue("@transacciones_total", canonicalTransactions);
        cmd.Parameters.AddWithValue("@transacciones_efectivo", canonicalCashTransactions);
        cmd.Parameters.AddWithValue("@transacciones_qr", canonicalQrTransactions);
        cmd.Parameters.AddWithValue("@transacciones_tarjeta", Math.Max(0, r.TransaccionesTarjeta));
        cmd.Parameters.AddWithValue("@transacciones_transferencia", Math.Max(0, r.TransaccionesTransferencia));
        cmd.Parameters.AddWithValue("@efectivo", canonicalCash);
        cmd.Parameters.AddWithValue("@qr", canonicalQr);
        cmd.Parameters.AddWithValue("@tarjeta", Math.Max(0, r.Tarjeta));
        cmd.Parameters.AddWithValue("@transferencia", Math.Max(0, r.Transferencia));
        cmd.Parameters.AddWithValue("@sin_metodo", Math.Max(0, r.SinMetodo));
        cmd.Parameters.AddWithValue("@productos_total", Math.Max(0, r.ProductosTotal));
        cmd.Parameters.AddWithValue("@mesas_total", Math.Max(0, r.MesasTotal));
        cmd.Parameters.AddWithValue("@minutos_jugados", Math.Max(0, r.MinutosJugados));
        cmd.Parameters.AddWithValue("@propinas_total", Math.Max(0, r.PropinasTotal));
        cmd.Parameters.AddWithValue("@cortesias_valor", Math.Max(0, r.CortesiasValor));
        cmd.Parameters.AddWithValue("@comisiones_total", Math.Max(0, r.ComisionesTotal));
        cmd.Parameters.AddWithValue("@gastos_total", Math.Max(0, r.GastosTotal));
        cmd.Parameters.AddWithValue("@perdidas_total", Math.Max(0, r.PerdidasTotal));
        cmd.Parameters.AddWithValue("@total_generado", canonicalTotal);
        cmd.Parameters.AddWithValue("@neto_turno", reconciledFromSales ? canonicalTotal - Math.Max(0, r.GastosTotal) : r.NetoTurno);
        cmd.Parameters.AddWithValue("@observaciones", r.Observaciones ?? "");
        cmd.Parameters.AddWithValue("@detalle_json", string.IsNullOrWhiteSpace(r.DetalleJson) ? "[]" : r.DetalleJson);
        cmd.Parameters.AddWithValue("@sync_key", syncKey);
        await cmd.ExecuteNonQueryAsync();
    }

    long id;
    await using (var idCmd = new MySqlCommand("SELECT id FROM cierres_turno WHERE sync_key=@sync_key LIMIT 1;", con))
    {
        idCmd.Parameters.AddWithValue("@sync_key", syncKey);
        id = Convert.ToInt64(await idCmd.ExecuteScalarAsync() ?? 0L);
    }

    await TrySyncSheets(db, sheets);
    return Results.Ok(new { ok = true, id, syncKey, reconciledFromSales, canonicalTransactions, canonicalCash, canonicalQr, canonicalTotal, message = reconciledFromSales ? "Arqueo guardado y conciliado contra ventas únicas de Railway." : "Arqueo guardado para Administración." });
});

app.MapGet("/api/admin/cierres-turno", async (Db db, string clave, int? sucursalId) =>
{
    const string adminKey = "ENTREGAR_LIMPIO_2026";
    if (clave != adminKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureShiftCloseTables(con);

    string sql = """
        SELECT
            id,
            sync_key,
            sucursal_id,
            sucursal,
            cajero_usuario,
            cajero_nombre,
            caja,
            turno,
            inicio,
            fin,
            hora_entrada,
            fecha_cierre,
            transacciones_total,
            transacciones_efectivo,
            transacciones_qr,
            transacciones_tarjeta,
            transacciones_transferencia,
            efectivo,
            qr,
            tarjeta,
            transferencia,
            sin_metodo,
            productos_total,
            mesas_total,
            minutos_jugados,
            propinas_total,
            cortesias_valor,
            comisiones_total,
            gastos_total,
            perdidas_total,
            total_generado,
            neto_turno,
            observaciones,
            detalle_json
        FROM cierres_turno
    """;

    if (sucursalId.HasValue && sucursalId.Value > 0)
        sql += " WHERE sucursal_id = @sucursal_id";

    sql += " ORDER BY fecha_cierre DESC, id DESC;";

    await using var cmd = new MySqlCommand(sql, con);
    if (sucursalId.HasValue && sucursalId.Value > 0)
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId.Value);

    List<Dictionary<string, object?>> rows = new();
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        Dictionary<string, object?> row = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rd.FieldCount; i++)
            row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
        rows.Add(row);
    }

    return Results.Ok(rows);
});

app.MapGet("/api/reportes/resumen", async (Db db) =>
{
    await using var con = await db.OpenAsync();

    const string sql = """
        SELECT
            CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
            COALESCE(SUM(v.total), 0) AS total_ventas,
            COUNT(v.id) AS cantidad_ventas
        FROM sucursales s
        LEFT JOIN ventas v ON v.sucursal_id = s.id
        GROUP BY s.id, s.nombre
        ORDER BY s.id;
    """;

    var porSucursal = await db.QueryAsync(con, sql);

    const string totalSql = """
        SELECT
            COALESCE(SUM(total), 0) AS total_general,
            COUNT(id) AS cantidad_ventas
        FROM ventas;
    """;

    var total = await db.QueryAsync(con, totalSql);

    return Results.Ok(new { porSucursal, total });
});

app.Run();

static async Task TrySyncSheets(Db db, SheetsReporter sheets)
{
    if (!sheets.IsConfigured) return;

    try
    {
        await sheets.SyncFromDatabaseAsync(db);
    }
    catch
    {
        // No se debe perder la venta si Google Sheets falla.
        // La venta ya queda guardada en MySQL y luego se puede forzar /api/sheets/sync.
    }
}


static async Task<IResult> AplicarStockTxtPaquetesV33(Db db, SheetsReporter sheets, string clave, int sucursalId)
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (sucursalId != 1 && sucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "sucursalId debe ser 1 o 2." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int productosActualizados = 0;
    int productosSinLimite = 0;
    var faltantes = new List<string>();
    var revisarUnidades = new List<string>();

    foreach (var item in StockSoloTxtV30())
    {
        long productoId = 0;
        int unidadesPorEntrada = 1;
        bool sinLimite = false;
        string nombreEncontrado = item.aliases[0];

        foreach (string alias in item.aliases)
        {
            await using var buscar = new MySqlCommand("""
                SELECT id,
                       GREATEST(COALESCE(unidades_por_entrada, 1), 1) AS unidades_por_entrada,
                       COALESCE(sin_limite_stock, 0) AS sin_limite_stock,
                       nombre
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND UPPER(TRIM(nombre)) = UPPER(TRIM(@nombre))
                  AND estado = 'ACTIVO'
                LIMIT 1;
            """, con);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@nombre", alias);

            await using var reader = await buscar.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                productoId = reader.GetInt64("id");
                unidadesPorEntrada = reader.GetInt32("unidades_por_entrada");
                sinLimite = reader.GetInt32("sin_limite_stock") == 1;
                nombreEncontrado = reader.GetString("nombre");
                break;
            }
        }

        if (productoId <= 0)
        {
            faltantes.Add(item.aliases[0]);
            continue;
        }

        if (sinLimite)
        {
            productosSinLimite++;
            continue;
        }

        if (unidadesPorEntrada == 1 && item.cantidad > 0)
            revisarUnidades.Add(nombreEncontrado);

        decimal stockTotal = Math.Max(0, item.cantidad * unidadesPorEntrada);

        await using var actualizar = new MySqlCommand("""
            UPDATE productos
            SET stock_actual = @stock_actual,
                tipo_entrada = CASE WHEN @unidades_por_entrada > 1 THEN 'PAQUETE' ELSE tipo_entrada END
            WHERE id = @id;
        """, con);
        actualizar.Parameters.AddWithValue("@stock_actual", stockTotal);
        actualizar.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
        actualizar.Parameters.AddWithValue("@id", productoId);
        await actualizar.ExecuteNonQueryAsync();
        productosActualizados++;
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        message = "Stock calculado desde el TXT como cantidad de paquetes/entradas por unidades_por_entrada.",
        formula = "stock_actual = cantidad_TXT × unidades_por_entrada",
        sucursalId,
        productosActualizados,
        productosSinLimite,
        faltantes,
        revisarUnidades,
        nota = "Los productos con unidades_por_entrada = 1 se calculan 1 a 1. Si realmente llegan en paquetes de 6, 12, 24, etc., configure primero ese valor desde Productos / Stock."
    });
}

static async Task<IResult> AplicarStockInicialReferenciaV32(Db db, SheetsReporter sheets, string clave, int sucursalId)
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (sucursalId != 1 && sucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "sucursalId debe ser 1 o 2." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int productosActualizados = 0;
    var faltantes = new List<string>();

    foreach (var item in StockInicialReferenciaV32())
    {
        long productoId = 0;

        foreach (string alias in item.aliases)
        {
            await using var buscar = new MySqlCommand("""
                SELECT id
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND UPPER(TRIM(nombre)) = UPPER(TRIM(@nombre))
                  AND estado = 'ACTIVO'
                LIMIT 1;
            """, con);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@nombre", alias);

            object? found = await buscar.ExecuteScalarAsync();
            if (found != null)
            {
                productoId = Convert.ToInt64(found);
                break;
            }
        }

        if (productoId <= 0)
        {
            faltantes.Add(item.aliases[0]);
            continue;
        }

        await using var actualizar = new MySqlCommand("""
            UPDATE productos
            SET stock_actual = @stock_actual
            WHERE id = @id;
        """, con);
        actualizar.Parameters.AddWithValue("@stock_actual", item.cantidad);
        actualizar.Parameters.AddWithValue("@id", productoId);
        await actualizar.ExecuteNonQueryAsync();
        productosActualizados++;
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        message = "Stock inicial cargado con las cantidades visibles en las capturas del inventario.",
        sucursalId,
        productosActualizados,
        faltantes,
        nota = "Solo se modifica stock_actual. No se cambian precios, categorías, presentaciones ni comisiones."
    });
}

static (decimal cantidad, string[] aliases)[] StockInicialReferenciaV32() => new (decimal cantidad, string[] aliases)[]
{
                (2m, new[] { "RON ABUELO", "ABUELO" }),
                (13m, new[] { "AGUA 2 LITROS", "AGUA 2L" }),
                (12m, new[] { "AGUA PERSONAL CON GAS", "AGUA CON GAS" }),
                (17m, new[] { "AGUA PERSONAL SIN GAS", "AGUA SIN GAS" }),
                (11m, new[] { "AGUA TONICA" }),
                (5m, new[] { "ALIKAL" }),
                (0m, new[] { "BELDEN" }),
                (44m, new[] { "BICO SABORES" }),
                (13m, new[] { "BLACK" }),
                (22m, new[] { "CERVEZA AMSTEL" }),
                (0m, new[] { "CERVEZA CONTI" }),
                (49m, new[] { "CERVEZA CORONA" }),
                (152m, new[] { "CERVEZA PACEÑA" }),
                (48m, new[] { "CERVEZA SKUL", "CERVEZA SKOL" }),
                (18m, new[] { "CICLON" }),
                (10m, new[] { "CIGARRO BOHEM DOUBLE GRANDE" }),
                (18m, new[] { "CIGARRO BOHEM UND" }),
                (0m, new[] { "CIGARRO BOHEN BLACK" }),
                (0m, new[] { "CIGARRO BOHEN SANDIA" }),
                (20m, new[] { "CIGARRO BOHEN UNIDAD" }),
                (5m, new[] { "CIGARRO BOHEN YOGOURT" }),
                (31m, new[] { "CIGARRO CAMEL ACTIVA UNID" }),
                (1m, new[] { "CIGARRO CAMEL ATIVO CHICO" }),
                (9m, new[] { "CIGARRO CAMEL GRANDE ACTIVA" }),
                (27m, new[] { "CIGARRO CAMEL SANDI UNIDAD" }),
                (11m, new[] { "CIGARRO CAMEL SANDIA CHICO" }),
                (11m, new[] { "CIGARRO CAMEL SANDIA GRANDE" }),
                (6m, new[] { "CIGARRO HILLS SANDI" }),
                (0m, new[] { "CIGARRO HILS" }),
                (16m, new[] { "CINCERO", "CENICERO" }),
                (89m, new[] { "CLORETS" }),
                (0m, new[] { "COCA EL BRUJO BICO STEVIA" }),
                (0m, new[] { "COCA EL BRUJO CHICLE", "COCA EL BRUJO CHILE" }),
                (3m, new[] { "COCA EL BRUJO MARACUYA" }),
                (3m, new[] { "COCA EL BRUJO MEDUSA" }),
                (2m, new[] { "COCA EL BRUJO RED BULL" }),
                (8m, new[] { "COCA EL BRUJO SANDIA RED BULL" }),
                (13m, new[] { "COCA EL BRUJO YOGOURT RED BULL" }),
                (0m, new[] { "COCA EL BRUJO YOGUBOLL" }),
                (6m, new[] { "COMBO FERNET" }),
                (10m, new[] { "COMBO FLOR DE CAÑA" }),
                (10m, new[] { "COMBO GIN" }),
                (10m, new[] { "COMBO HABANA" }),
                (15m, new[] { "COPAS DE VINO" }),
                (4m, new[] { "DOCILE MINTY" }),
                (5m, new[] { "ENCENDEDOR" }),
                (3m, new[] { "FERNET" }),
                (4m, new[] { "FLOW ACHACHAIRU" }),
                (0m, new[] { "FLOW CHUFLAY" }),
                (11m, new[] { "FLOW SIN AZUCAR" }),
                (19m, new[] { "FOUR LOCO" }),
                (2m, new[] { "GIN ROSADO" }),
                (63m, new[] { "GROSSO" }),
                (13m, new[] { "HALLS" }),
                (24m, new[] { "ICE 51" }),
                (8m, new[] { "NACHO NORMAL" }),
                (9m, new[] { "NACHOS PICANTES" }),
                (8m, new[] { "NOCHE ICE" }),
                (12m, new[] { "NACHO MAX QUESO" }),
                (10m, new[] { "PAPAS NORMALES" }),
                (0m, new[] { "PAPAS PICANTES" }),
                (28m, new[] { "PASTILLAS EUCALIPTO" }),
                (12m, new[] { "PASTILLAS MINT" }),
                (12m, new[] { "POWER CHICO" }),
                (6m, new[] { "POWER GRANDE" }),
                (20m, new[] { "PROMO AMSTEL" }),
                (30m, new[] { "PROMO AMSTEL X 3" }),
                (20m, new[] { "PROMO CONTI" }),
                (30m, new[] { "PROMO CONTI X 3" }),
                (20m, new[] { "PROMO CORONA" }),
                (50m, new[] { "PROMO PACEÑA" }),
                (17m, new[] { "PROMO VASO DE FERNET" }),
                (6m, new[] { "PAPA NAX" }),
                (8m, new[] { "PIZONES CHOCOLATE" }),
                (10m, new[] { "PIZONES PICANTES" }),
                (2m, new[] { "PLATANITO CHIPS" }),
                (1m, new[] { "QUISQUE BLACK LABEL", "WHIKY BLACK LABEL" }),
                (23m, new[] { "RED BULL" }),
                (1m, new[] { "RON DE COCO OLD" }),
                (6m, new[] { "RON FLOR DE CAÑA" }),
                (3m, new[] { "RON HABANA 7 AÑOS" }),
                (7m, new[] { "SANTE GRANDE" }),
                (7m, new[] { "SANTE PEQUEÑO" }),
                (10m, new[] { "SODA COCA COLA 2 LITROS" }),
                (7m, new[] { "SODA COCA COLA 3 LITROS" }),
                (13m, new[] { "SODA FANTA 2 LITROS" }),
                (13m, new[] { "SODA PEQUE COCA COLA VARIOS" }),
                (10m, new[] { "SODA SPRITE 2 LITROS" }),
                (7m, new[] { "TAKIS" }),
                (2m, new[] { "TEQUILA JOSE CUERVO" }),
                (51m, new[] { "VASO DE FERNET + COCA COLA 2L E 3L" }),
                (10m, new[] { "VASO DE RON" }),
                (10m, new[] { "VASO DE WISKIE", "VASO DE WHISKY" }),
                (4m, new[] { "VASO TEQUILERO" }),
                (37m, new[] { "VASOS CERVECEROS" }),
                (20m, new[] { "VASOS DE SODA" }),
                (4m, new[] { "VINO BLANCO" }),
                (17m, new[] { "VINO TINTO" }),
                (64m, new[] { "CHICLE" }),
                (15m, new[] { "CHICLE GRANDE" }),
                (30m, new[] { "CHICLE PEQUEÑO" }),
                (1m, new[] { "CHUPETE" }),
                (0m, new[] { "MABEL" }),
                (4m, new[] { "MIX NAX" }),
};


static (decimal cantidad, string[] aliases)[] StockSoloTxtV30() => new (decimal cantidad, string[] aliases)[]
{
    (9m, new[] { "AGUA 2 LITROS", "AGUA 2L" }),
    (11m, new[] { "AGUA PERSONAL CON GAS", "AGUA CON GAS" }),
    (21m, new[] { "AGUA PERSONAL SIN GAS", "AGUA SIN GAS" }),
    (11m, new[] { "AGUA TONICA" }),
    (13m, new[] { "SANTE GRANDE" }),
    (7m, new[] { "SANTE PEQUEÑO" }),
    (7m, new[] { "BLACK" }),
    (14m, new[] { "CICLON" }),
    (5m, new[] { "POWER CHICO" }),
    (4m, new[] { "POWER GRANDE" }),
    (22m, new[] { "RED BULL" }),
    (4m, new[] { "SODA COCA COLA 2 LITROS", "COCA COLA 2L" }),
    (5m, new[] { "SODA COCA COLA 3 LITROS", "COCA COLA 3L" }),
    (12m, new[] { "SODA FANTA 2 LITROS", "FANTA 2L" }),
    (36m, new[] { "SODA PEQUE COCA COLA VARIOS", "PEQUE" }),
    (15m, new[] { "SODA SPRITE 2 LITROS", "SPRITE 2L" }),
    (5m, new[] { "COCA AMAIRE N" }),
    (15m, new[] { "COCA EL BRUJO BICO STEVIA", "COCA BICO ESTEBIA" }),
    (9m, new[] { "COCA EL BRUJO MARACUYA", "COCA MARACUYA" }),
    (15m, new[] { "COCA EL BRUJO MEDUSA", "COCA MEDUSA" }),
    (28m, new[] { "COCA MEDUSA N" }),
    (8m, new[] { "COCA EL BRUJO RED BULL", "COCA REDBUL" }),
    (3m, new[] { "COCA EL BRUJO SANDIA RED BULL", "COCA SANDIA REDBUL" }),
    (8m, new[] { "COCA EL BRUJO YOGUBOLL", "COCA YOGUBOL" }),
    (4m, new[] { "COCA YOGUBOL N" }),
    (8m, new[] { "COCA EL BRUJO YOGOURT RED BULL", "COCA YOGURT REDBUL" }),
    (10m, new[] { "CIGARRO BOHEN BLACK", "BOHEM BLACK" }),
    (16m, new[] { "CIGARRO BOHEN SANDIA", "BOHEM SANDIA" }),
    (0m, new[] { "CIGARRO BOHEN YOGOURT", "BOHEM YOGURT" }),
    (9m, new[] { "CIGARRO CAMEL ACTIVA UNID", "CAMEL ACTIVA" }),
    (1m, new[] { "CIGARRO CAMEL ATIVO CHICO", "CAMEL CHICO ACTIVA" }),
    (43m, new[] { "CIGARRO CAMEL SANDIA CHICO", "CAMEL CHICO SANDIA" }),
    (13m, new[] { "CIGARRO CAMEL SANDIA GRANDE", "CAMEL SANDIA" }),
    (10m, new[] { "CIGARRO HILS", "HILLS" }),
    (10m, new[] { "CIGARRO HILLS SANDI", "HILLS SANDIA" }),
    (1m, new[] { "CERVEZA AMSTEL" }),
    (16m, new[] { "CERVEZA CORONA" }),
    (44m, new[] { "CERVEZA SKUL", "CERVEZA SKOL" }),
    (1m, new[] { "AMARULA" }),
    (2m, new[] { "FERNET" }),
    (15m, new[] { "RON FLOR DE CAÑA", "FLOR DE CAÑA" }),
    (22m, new[] { "FLOW ACHACHAIRU" }),
    (12m, new[] { "FLOW CHUFLAY" }),
    (12m, new[] { "FLOW NENE" }),
    (17m, new[] { "FOUR LOCO" }),
    (2m, new[] { "GIN ROSADO", "GIN" }),
    (3m, new[] { "RON HABANA 7 AÑOS", "HAVANA" }),
    (20m, new[] { "ICE 51" }),
    (1m, new[] { "NOCHE ICE" }),
    (0m, new[] { "RON DE COCO OLD", "OLD" }),
    (2m, new[] { "RON ABUELO", "ABUELO" }),
    (2m, new[] { "TEQUILA JOSE CUERVO", "TEQUILA" }),
    (4m, new[] { "VINO BLANCO" }),
    (14m, new[] { "VINO TINTO" }),
    (1m, new[] { "QUISQUE BLACK LABEL", "WHIKY BLACK LABEL" }),
    (2m, new[] { "COMBO FERNET" }),
    (3m, new[] { "COMBO FLOR DE CAÑA" }),
    (2m, new[] { "COMBO RON ABUELO" }),
    (10m, new[] { "VASO CHUFLAY" }),
    (10m, new[] { "VASO FERNET" }),
    (10m, new[] { "VASO FLOR DE CAÑA" }),
    (10m, new[] { "VASO RUM/RON ABUELO" }),
    (10m, new[] { "VASO TEQUILA" }),
    (10m, new[] { "VASO VINO" }),
    (10m, new[] { "VASO VINO tinto" }),
    (10m, new[] { "VASO WHISKY" }),
    (0m, new[] { "NACHO LIMON" }),
    (8m, new[] { "NACHO NORMAL" }),
    (7m, new[] { "NACHOS PICANTES", "NACHO PICANTE" }),
    (10m, new[] { "NACHO MAX QUESO", "NACHO SABOR QUESO" }),
    (13m, new[] { "PAPA CHURRAZCO" }),
    (4m, new[] { "PAPA NAX", "PAPA NAX NORMALES" }),
    (4m, new[] { "PIZONES CHOCOLATE", "PINZONES CHOCOLATE" }),
    (9m, new[] { "PIZONES PICANTES", "PIZONES PICANTE" }),
    (5m, new[] { "PLATANITO CHIPS", "PLATANITO" }),
    (0m, new[] { "TAKIS" }),
    (0m, new[] { "ARCOR" }),
    (0m, new[] { "BELDEN" }),
    (44m, new[] { "CHICLE" }),
    (11m, new[] { "CHICLE GRANDE" }),
    (10m, new[] { "CHICLE PEQUEÑO" }),
    (0m, new[] { "CHUPETE" }),
    (73m, new[] { "CLORETS" }),
    (14m, new[] { "COCA EL BRUJO CHICLE", "COCA CHICLE" }),
    (4m, new[] { "COCA CHICLE N" }),
    (12m, new[] { "PASTILLAS EUCALIPTO", "EUCALIPTO" }),
    (24m, new[] { "GROSSO", "GROSO" }),
    (10m, new[] { "HALLS" }),
    (0m, new[] { "MABEL" }),
    (6m, new[] { "PASTILLAS MINT", "MINT" }),
    (4m, new[] { "DOCILE MINTY", "MINTY" }),
    (2m, new[] { "ALIKAL" }),
    (15m, new[] { "COPAS DE VINO", "COPA" }),
    (14m, new[] { "MESAS" }),
    (30m, new[] { "SILLAS" }),
    (10m, new[] { "VASO DE WISKIE", "VASOS DE WISKI" }),
    (4m, new[] { "VASO TEQUILERO", "VASOS TEQUILERO" }),
    (86m, new[] { "VICO" }),
};

static (string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)[] CatalogoFinalV29() => new (string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)[]
{
    ("AGUA 2L", "Agua", 9m, 20.00m, false),
    ("AGUA CON GAS", "Agua", 11m, 10.00m, false),
    ("AGUA SIN GAS", "Agua", 21m, 10.00m, false),
    ("AGUA TONICA", "Agua", 11m, 20.00m, false),
    ("SANTE GRANDE", "Agua", 13m, 25.00m, false),
    ("SANTE PEQUEÑO", "Agua", 7m, 18.00m, false),
    ("BLACK", "Energizantes", 7m, 20.00m, false),
    ("CICLON", "Energizantes", 14m, 20.00m, false),
    ("POWER CHICO", "Energizantes", 5m, 15.00m, false),
    ("POWER GRANDE", "Energizantes", 4m, 25.00m, false),
    ("RED BULL", "Energizantes", 22m, 30.00m, false),
    ("COCA COLA 2L", "Sodas", 4m, 25.00m, false),
    ("COCA COLA 3L", "Sodas", 5m, 30.00m, false),
    ("FANTA 2L", "Sodas", 12m, 25.00m, false),
    ("PEQUE", "Sodas", 36m, 6.00m, false),
    ("SPRITE 2L", "Sodas", 15m, 25.00m, false),
    ("COCA AMAIRE N", "Coca machucada", 5m, 55.00m, false),
    ("COCA BICO ESTEBIA", "Coca machucada", 15m, 55.00m, false),
    ("COCA MARACUYA", "Coca machucada", 9m, 55.00m, false),
    ("COCA MEDUSA", "Coca machucada", 15m, 65.00m, false),
    ("COCA MEDUSA N", "Coca machucada", 28m, 65.00m, false),
    ("COCA REDBUL", "Coca machucada", 8m, 55.00m, false),
    ("COCA SANDIA REDBUL", "Coca machucada", 3m, 55.00m, false),
    ("COCA YOGUBOL", "Coca machucada", 8m, 55.00m, false),
    ("COCA YOGUBOL N", "Coca machucada", 4m, 55.00m, false),
    ("COCA YOGURT REDBUL", "Coca machucada", 8m, 55.00m, false),
    ("BOHEM BLACK", "Cigarros", 10m, 30.00m, false),
    ("BOHEM SANDIA", "Cigarros", 16m, 25.00m, false),
    ("BOHEM YOGURT", "Cigarros", 0m, 25.00m, false),
    ("CAMEL ACTIVA", "Cigarros", 9m, 2.00m, false),
    ("CAMEL CHICO ACTIVA", "Cigarros", 1m, 18.00m, false),
    ("CAMEL CHICO SANDIA", "Cigarros", 43m, 20.00m, false),
    ("CAMEL SANDIA", "Cigarros", 13m, 30.00m, false),
    ("HILLS", "Cigarros", 10m, 18.00m, false),
    ("HILLS SANDIA", "Cigarros", 10m, 18.00m, false),
    ("CERVEZA AMSTEL", "Cervezas", 1m, 22.00m, false),
    ("CERVEZA CORONA", "Cervezas", 16m, 25.00m, false),
    ("CERVEZA SKOL", "Cervezas", 44m, 10.00m, false),
    ("AMARULA", "Tragos / Botellas", 1m, 50.00m, false),
    ("FERNET", "Tragos / Botellas", 2m, 275.00m, false),
    ("FLOR DE CAÑA", "Tragos / Botellas", 15m, 275.00m, false),
    ("FLOW ACHACHAIRU", "Tragos / Botellas", 22m, 25.00m, false),
    ("FLOW CHUFLAY", "Tragos / Botellas", 12m, 25.00m, false),
    ("FLOW NENE", "Tragos / Botellas", 12m, 25.00m, false),
    ("FOUR LOCO", "Tragos / Botellas", 17m, 70.00m, false),
    ("GIN", "Tragos / Botellas", 2m, 275.00m, false),
    ("HAVANA", "Tragos / Botellas", 3m, 425.00m, false),
    ("ICE 51", "Tragos / Botellas", 20m, 30.00m, false),
    ("NOCHE ICE", "Tragos / Botellas", 1m, 25.00m, false),
    ("OLD", "Tragos / Botellas", 0m, 300.00m, false),
    ("RON ABUELO", "Tragos / Botellas", 2m, 300.00m, false),
    ("TEQUILA", "Tragos / Botellas", 2m, 200.00m, false),
    ("VINO BLANCO", "Tragos / Botellas", 4m, 50.00m, false),
    ("VINO TINTO", "Tragos / Botellas", 14m, 50.00m, false),
    ("WHIKY BLACK LABEL", "Tragos / Botellas", 1m, 800.00m, false),
    ("COMBO FERNET", "Combos / Promos", 2m, 320.00m, false),
    ("COMBO FLOR DE CAÑA", "Combos / Promos", 3m, 310.00m, false),
    ("COMBO RON ABUELO", "Combos / Promos", 2m, 340.00m, false),
    ("VASO CHUFLAY", "Servidos en vaso", 10m, 25.00m, true),
    ("VASO FERNET", "Servidos en vaso", 10m, 25.00m, true),
    ("VASO FLOR DE CAÑA", "Servidos en vaso", 10m, 25.00m, true),
    ("VASO RUM/RON ABUELO", "Servidos en vaso", 10m, 25.00m, true),
    ("VASO TEQUILA", "Servidos en vaso", 10m, 20.00m, true),
    ("VASO VINO", "Servidos en vaso", 10m, 15.00m, true),
    ("VASO VINO tinto", "Servidos en vaso", 10m, 15.00m, true),
    ("VASO WHISKY", "Servidos en vaso", 10m, 35.00m, true),
    ("NACHO LIMON", "Snacks y piqueos", 0m, 5.00m, false),
    ("NACHO NORMAL", "Snacks y piqueos", 8m, 5.00m, false),
    ("NACHO PICANTE", "Snacks y piqueos", 7m, 5.00m, false),
    ("NACHO SABOR QUESO", "Snacks y piqueos", 10m, 5.00m, false),
    ("PAPA CHURRAZCO", "Snacks y piqueos", 13m, 5.00m, false),
    ("PAPA NAX NORMALES", "Snacks y piqueos", 4m, 5.00m, false),
    ("PINZONES CHOCOLATE", "Snacks y piqueos", 4m, 5.00m, false),
    ("PIZONES PICANTE", "Snacks y piqueos", 9m, 5.00m, false),
    ("PLATANITO", "Snacks y piqueos", 5m, 5.00m, false),
    ("TAKIS", "Snacks y piqueos", 0m, 8.00m, false),
    ("ARCOR", "Dulces y golosinas", 0m, 1.00m, false),
    ("BELDEN", "Dulces y golosinas", 0m, 8.00m, false),
    ("CHICLE", "Dulces y golosinas", 44m, 1.00m, false),
    ("CHICLE GRANDE", "Dulces y golosinas", 11m, 4.00m, false),
    ("CHICLE PEQUEÑO", "Dulces y golosinas", 10m, 1.00m, false),
    ("CHUPETE", "Dulces y golosinas", 0m, 2.00m, false),
    ("CLORETS", "Dulces y golosinas", 73m, 1.00m, false),
    ("COCA CHICLE", "Dulces y golosinas", 14m, 55.00m, false),
    ("COCA CHICLE N", "Dulces y golosinas", 4m, 55.00m, false),
    ("EUCALIPTO", "Dulces y golosinas", 12m, 0.50m, false),
    ("GROSO", "Dulces y golosinas", 24m, 1.00m, false),
    ("HALLS", "Dulces y golosinas", 10m, 8.00m, false),
    ("MABEL", "Dulces y golosinas", 0m, 6.00m, false),
    ("MINT", "Dulces y golosinas", 6m, 0.50m, false),
    ("MINTY", "Dulces y golosinas", 4m, 5.00m, false),
    ("ALIKAL", "Otros / Extras", 2m, 10.00m, false),
    ("COPA", "Otros / Extras", 15m, 10.00m, false),
    ("MESAS", "Otros / Extras", 14m, 0.00m, false),
    ("SILLAS", "Otros / Extras", 30m, 0.00m, false),
    ("VASOS DE WISKI", "Otros / Extras", 10m, 10.00m, false),
    ("VASOS TEQUILERO", "Otros / Extras", 4m, 10.00m, false),
    ("VICO", "Otros / Extras", 86m, 1.00m, false)
};

static (string nombre, string categoria, decimal precio)[] CatalogoProductosLocalV19() => new (string nombre, string categoria, decimal precio)[]
{
    ("AGUA 2 LITROS", "Bebidas", 20.00m),
    ("AGUA PERSONAL CON GAS", "Bebidas", 10.00m),
    ("AGUA PERSONAL SIN GAS", "Bebidas", 10.00m),
    ("AGUA TONICA", "Bebidas", 20.00m),
    ("CICLON", "Bebidas", 20.00m),
    ("COCA EL BRUJO MARACUYA", "Bebidas", 25.00m),
    ("COCA EL BRUJO MEDUSA", "Bebidas", 35.00m),
    ("COCA EL BRUJO RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO SANDIA RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO YOGOURT RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO YOGUBOLL", "Bebidas", 25.00m),
    ("FLOW ACHACHAIRU", "Bebidas", 25.00m),
    ("FLOW CHUFLAY", "Bebidas", 25.00m),
    ("FLOW SIN AZUCAR", "Bebidas", 25.00m),
    ("POWER CHICO", "Bebidas", 18.00m),
    ("POWER GRANDE", "Bebidas", 25.00m),
    ("RED BULL", "Bebidas", 30.00m),
    ("SODA COCA COLA 2 LITROS", "Bebidas", 25.00m),
    ("SODA COCA COLA 3 LITROS", "Bebidas", 30.00m),
    ("SODA FANTA 2 LITROS", "Bebidas", 25.00m),
    ("SODA PEQUE COCA COLA VARIOS", "Bebidas", 6.00m),
    ("SODA SPRITE 2 LITROS", "Bebidas", 25.00m),
    ("VASOS DE SODA", "Bebidas", 10.00m),
    ("CERVEZA AMSTEL", "Cervezas", 22.00m),
    ("CERVEZA CONTI", "Cervezas", 20.00m),
    ("CERVEZA CORONA", "Cervezas", 25.00m),
    ("CERVEZA PACEÑA", "Cervezas", 30.00m),
    ("CERVEZA SKUL", "Cervezas", 10.00m),
    ("BLACK", "Botellas/Tragos", 20.00m),
    ("FERNET", "Botellas/Tragos", 275.00m),
    ("FOUR LOCO", "Botellas/Tragos", 70.00m),
    ("GIN ROSADO", "Botellas/Tragos", 275.00m),
    ("ICE 51", "Botellas/Tragos", 30.00m),
    ("NOCHE ICE", "Botellas/Tragos", 25.00m),
    ("QUISQUE BLACK LABEL", "Botellas/Tragos", 800.00m),
    ("RON ABUELO", "Botellas/Tragos", 300.00m),
    ("RON DE COCO OLD", "Botellas/Tragos", 300.00m),
    ("RON FLOR DE CAÑA", "Botellas/Tragos", 275.00m),
    ("RON HABANA 7 AÑOS", "Botellas/Tragos", 425.00m),
    ("TEQUILA JOSE CUERVO", "Botellas/Tragos", 200.00m),
    ("VASO DE FERNET + COCA COLA 2L E 3L", "Botellas/Tragos", 20.00m),
    ("VASO DE RON", "Botellas/Tragos", 20.00m),
    ("VASO DE WISKIE", "Botellas/Tragos", 10.00m),
    ("VINO BLANCO", "Botellas/Tragos", 50.00m),
    ("VINO TINTO", "Botellas/Tragos", 50.00m),
    ("CIGARRO BOHEM DOUBLE GRANDE", "Cigarros", 30.00m),
    ("CIGARRO BOHEM UND", "Cigarros", 2.00m),
    ("CIGARRO BOHEN BLACK", "Cigarros", 30.00m),
    ("CIGARRO BOHEN SANDIA", "Cigarros", 25.00m),
    ("CIGARRO BOHEN UNIDAD", "Cigarros", 2.00m),
    ("CIGARRO BOHEN YOGOURT", "Cigarros", 25.00m),
    ("CIGARRO CAMEL ACTIVA UNID", "Cigarros", 2.00m),
    ("CIGARRO CAMEL ATIVO CHICO", "Cigarros", 18.00m),
    ("CIGARRO CAMEL GRANDE ACTIVA", "Cigarros", 30.00m),
    ("CIGARRO CAMEL SANDI UNIDAD", "Cigarros", 2.00m),
    ("CIGARRO CAMEL SANDIA CHICO", "Cigarros", 20.00m),
    ("CIGARRO CAMEL SANDIA GRANDE", "Cigarros", 30.00m),
    ("CIGARRO HILLS SANDI", "Cigarros", 18.00m),
    ("CIGARRO HILS", "Cigarros", 18.00m),
    ("BICO SABORES", "Dulces", 5.00m),
    ("CHICLE", "Dulces", 1.00m),
    ("CHICLE GRANDE", "Dulces", 4.00m),
    ("CHICLE PEQUEÑO", "Dulces", 1.00m),
    ("CHUPETE", "Dulces", 2.00m),
    ("CLORETS", "Dulces", 1.00m),
    ("COCA EL BRUJO BICO STEVIA", "Dulces", 25.00m),
    ("COCA EL BRUJO CHICLE", "Dulces", 25.00m),
    ("DOCILE MINTY", "Dulces", 5.00m),
    ("GROSSO", "Dulces", 1.00m),
    ("HALLS", "Dulces", 8.00m),
    ("MABEL", "Dulces", 6.00m),
    ("PASTILLAS EUCALIPTO", "Dulces", 0.50m),
    ("PASTILLAS MINT", "Dulces", 0.50m),
    ("MIX NAX", "Snacks", 8.00m),
    ("NACHO MAX QUESO", "Snacks", 5.00m),
    ("NACHO NORMAL", "Snacks", 5.00m),
    ("NACHOS PICANTES", "Snacks", 5.00m),
    ("PAPA NAX", "Snacks", 5.00m),
    ("PAPAS NORMALES", "Snacks", 5.00m),
    ("PAPAS PICANTES", "Snacks", 5.00m),
    ("PIZONES CHOCOLATE", "Snacks", 5.00m),
    ("PIZONES PICANTES", "Snacks", 5.00m),
    ("PLATANITO CHIPS", "Snacks", 5.00m),
    ("SANTE GRANDE", "Snacks", 25.00m),
    ("SANTE PEQUEÑO", "Snacks", 18.00m),
    ("TAKIS", "Snacks", 8.00m),
    ("CINCERO", "Vasos/Accesorios", 10.00m),
    ("COPAS DE VINO", "Vasos/Accesorios", 10.00m),
    ("ENCENDEDOR", "Vasos/Accesorios", 3.00m),
    ("VASO TEQUILERO", "Vasos/Accesorios", 10.00m),
    ("VASOS CERVECEROS", "Vasos/Accesorios", 10.00m),
    ("ALIKAL", "Varios", 10.00m),
    ("BELDEN", "Varios", 8.00m)
};

static (string nombre, string categoria, decimal precio, string detalle)[] CatalogoCombosPromosLocalV19() => new (string nombre, string categoria, decimal precio, string detalle)[]
{
    ("COMBO FERNET", "Combo", 300.00m, "1x FERNET + 1x SODA COCA COLA 2 LITROS"),
    ("COMBO FLOR DE CAÑA", "Combo", 300.00m, "1x RON FLOR DE CAÑA + 1x SODA COCA COLA 2 LITROS"),
    ("COMBO GIN", "Combo", 300.00m, "1x SANTE GRANDE + 1x GIN ROSADO"),
    ("COMBO HABANA", "Combo", 450.00m, "1x RON HABANA 7 AÑOS + 1x SODA COCA COLA 2 LITROS"),
    ("PROMO AMSTEL", "Promoción", 100.00m, "5x CERVEZA AMSTEL"),
    ("PROMO AMSTEL X 3", "Promoción", 60.00m, "3x CERVEZA AMSTEL"),
    ("PROMO CONTI", "Promoción", 100.00m, "5x CERVEZA CONTI"),
    ("PROMO CONTI X 3", "Promoción", 60.00m, "3x CERVEZA CONTI"),
    ("PROMO CORONA", "Promoción", 110.00m, "5x CERVEZA CORONA"),
    ("PROMO PACEÑA", "Promoción", 120.00m, "5x CERVEZA PACEÑA"),
    ("PROMO VASO DE FERNET", "Promoción", 15.00m, "1x VASO DE FERNET")
};

static async Task<bool> UpsertCatalogoFinalV29(MySqlConnection con, int sucursalId, string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)
{
    categoria = NormalizarCategoriaProducto(categoria, nombre);
    long productoId = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND nombre = @nombre LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) productoId = Convert.ToInt64(found);
    }

    bool existed = productoId > 0;
    decimal stockActual = sinLimiteStock ? 0 : cantidad;
    decimal stockMinimo = sinLimiteStock ? 0 : 30;
    string unidadBase = sinLimiteStock ? "SIN LÍMITE" : "UNIDAD";

    if (!existed)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos
                (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado, genera_comision, tipo_comision, valor_comision, sin_limite_stock)
            VALUES
                (@sucursal_id, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, 'ACTIVO', @genera_comision, @tipo_comision, @valor_comision, @sin_limite_stock);
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", stockActual);
        cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        cmd.Parameters.AddWithValue("@sin_limite_stock", sinLimiteStock ? 1 : 0);
        productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria = @categoria,
                unidad_base = @unidad_base,
                stock_actual = @stock_actual,
                stock_minimo = @stock_minimo,
                estado = 'ACTIVO',
                sin_limite_stock = @sin_limite_stock
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", stockActual);
        cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
        cmd.Parameters.AddWithValue("@sin_limite_stock", sinLimiteStock ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", productoId);
        await cmd.ExecuteNonQueryAsync();
    }

    long presId = 0;
    await using (var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND estado = 'ACTIVO' LIMIT 1;", con))
    {
        buscarPres.Parameters.AddWithValue("@producto_id", productoId);
        object? foundPres = await buscarPres.ExecuteScalarAsync();
        if (foundPres != null) presId = Convert.ToInt64(foundPres);
    }

    if (presId <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
            VALUES (@producto_id, 'Unidad', 1, @precio_venta, 'ACTIVO');
        """, con);
        cmd.Parameters.AddWithValue("@producto_id", productoId);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        await cmd.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE presentaciones
            SET nombre = 'Unidad',
                cantidad_base = 1,
                precio_venta = @precio_venta,
                estado = 'ACTIVO'
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        cmd.Parameters.AddWithValue("@id", presId);
        await cmd.ExecuteNonQueryAsync();
    }

    return existed;
}

static async Task<bool> UpsertCatalogoProductoLocal(MySqlConnection con, int sucursalId, string nombre, string categoria, decimal precio, string presentacion, int minimo)
{
    categoria = NormalizarCategoriaProducto(categoria, nombre);
    long productoId = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND nombre = @nombre LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) productoId = Convert.ToInt64(found);
    }

    bool existed = productoId > 0;

    if (!existed)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos
                (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado, genera_comision, tipo_comision, valor_comision)
            VALUES
                (@sucursal_id, @nombre, @categoria, 'UNIDAD', 0, @minimo, 'ACTIVO', @genera_comision, @tipo_comision, @valor_comision);
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@minimo", minimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria = @categoria,
                unidad_base = 'UNIDAD',
                stock_minimo = @minimo,
                estado = 'ACTIVO',
                genera_comision = @genera_comision,
                tipo_comision = @tipo_comision,
                valor_comision = @valor_comision
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@minimo", minimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        cmd.Parameters.AddWithValue("@id", productoId);
        await cmd.ExecuteNonQueryAsync();
    }

    long presId = 0;
    await using (var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND estado = 'ACTIVO' LIMIT 1;", con))
    {
        buscarPres.Parameters.AddWithValue("@producto_id", productoId);
        object? foundPres = await buscarPres.ExecuteScalarAsync();
        if (foundPres != null) presId = Convert.ToInt64(foundPres);
    }

    if (presId <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
            VALUES (@producto_id, @nombre, 1, @precio_venta, 'ACTIVO');
        """, con);
        cmd.Parameters.AddWithValue("@producto_id", productoId);
        cmd.Parameters.AddWithValue("@nombre", presentacion);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        await cmd.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE presentaciones
            SET nombre = @nombre,
                cantidad_base = 1,
                precio_venta = @precio_venta,
                estado = 'ACTIVO'
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@nombre", presentacion);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        cmd.Parameters.AddWithValue("@id", presId);
        await cmd.ExecuteNonQueryAsync();
    }

    return existed;
}

static bool EsProductoConComision(string nombre)
{
    string n = (nombre ?? "").ToUpperInvariant();
    return n.Contains("RON") || n.Contains("TEQUILA") || n.Contains("GIN") || n.Contains("FERNET") || n.Contains("WHISK") || n.Contains("WISKIE") || n.Contains("ABUELO") || n.Contains("HABANA") || n.Contains("BLACK LABEL");
}


static async Task UpdateUserPasswordHash(MySqlConnection con, int userId, string plainPassword)
{
    await using var cmd = new MySqlCommand("UPDATE usuarios SET clave = @clave WHERE id = @id;", con);
    cmd.Parameters.AddWithValue("@clave", PasswordHasher.Hash(plainPassword));
    cmd.Parameters.AddWithValue("@id", userId);
    await cmd.ExecuteNonQueryAsync();
}

static async Task HashPlainUserPasswords(MySqlConnection con)
{
    var pendientes = new List<(int id, string clave)>();

    await using (var cmd = new MySqlCommand("SELECT id, clave FROM usuarios;", con))
    await using (var rd = await cmd.ExecuteReaderAsync())
    {
        while (await rd.ReadAsync())
        {
            string clave = rd.IsDBNull(rd.GetOrdinal("clave")) ? "" : rd.GetString("clave");
            if (!PasswordHasher.IsHashed(clave))
                pendientes.Add((rd.GetInt32("id"), clave));
        }
    }

    foreach (var item in pendientes)
    {
        await using var update = new MySqlCommand("UPDATE usuarios SET clave = @clave WHERE id = @id;", con);
        update.Parameters.AddWithValue("@clave", PasswordHasher.Hash(item.clave));
        update.Parameters.AddWithValue("@id", item.id);
        await update.ExecuteNonQueryAsync();
    }
}


static async Task<(bool ok, string message)> AcquireSaleGuardsAsync(MySqlConnection con, MySqlTransaction tx, VentaRequest venta, string operationKey)
{
    static string HashKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(hash).Substring(0, 32);
    }

    var lockNames = new SortedSet<string>(StringComparer.Ordinal);
    lockNames.Add("SALEOP_" + HashKey(operationKey));

    string tipo = (venta.Tipo ?? "").Trim().ToUpperInvariant();
    if (tipo == "MESA" && venta.SessionId.HasValue && venta.SessionId.Value > 0)
        lockNames.Add($"MESAFINAL_{venta.SucursalId}_{venta.SessionId.Value}");

    if (venta.Detalle != null)
    {
        foreach (var d in venta.Detalle)
        {
            string ck = (d.ConsumptionKey ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(ck))
                lockNames.Add("CONSUMO_" + HashKey(ck));
        }
    }

    foreach (string lockName in lockNames)
    {
        await using var cmd = new MySqlCommand("SELECT GET_LOCK(@name, 10);", con, tx);
        cmd.Parameters.AddWithValue("@name", lockName.Length > 64 ? lockName.Substring(0, 64) : lockName);
        object? result = await cmd.ExecuteScalarAsync();
        if (result == null || Convert.ToInt32(result) != 1)
            return (false, "Otra operación igual se está procesando. El cobro fue bloqueado temporalmente para evitar duplicación; vuelva a intentar una sola vez.");
    }

    return (true, "OK");
}

static async Task EnsureLedgerForExistingSaleAsync(MySqlConnection con, MySqlTransaction tx, long ventaId, VentaRequest venta, string syncKey, string operationKey)
{
    await using var cmd = new MySqlCommand("""
        INSERT IGNORE INTO libro_caja
        (venta_id, sucursal_id, cajero, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, session_id, estado)
        VALUES
        (@venta_id, @sucursal_id, @cajero, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), @session_id, 'CONFIRMADA');
    """, con, tx);
    cmd.Parameters.AddWithValue("@venta_id", ventaId);
    cmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
    cmd.Parameters.AddWithValue("@cajero", venta.Cajero ?? "");
    cmd.Parameters.AddWithValue("@fecha", venta.Fecha);
    cmd.Parameters.AddWithValue("@tipo", venta.Tipo ?? "VENTA");
    cmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago ?? "");
    cmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
    cmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
    cmd.Parameters.AddWithValue("@total", venta.Total);
    cmd.Parameters.AddWithValue("@sync_key", syncKey);
    cmd.Parameters.AddWithValue("@operation_key", operationKey);
    cmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureAccountingLedger(MySqlConnection con)
{
    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS libro_caja (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            venta_id BIGINT NOT NULL,
            sucursal_id INT NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            caja_nombre VARCHAR(60) NULL,
            turno VARCHAR(20) NULL,
            fecha DATETIME NOT NULL,
            tipo VARCHAR(60) NOT NULL,
            metodo_pago VARCHAR(30) NOT NULL,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            total DECIMAL(12,2) NOT NULL,
            sync_key VARCHAR(220) NOT NULL,
            operation_key VARCHAR(220) NULL,
            session_id INT NULL,
            estado VARCHAR(20) NOT NULL DEFAULT 'CONFIRMADA',
            creado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UNIQUE KEY uk_libro_venta (venta_id),
            UNIQUE KEY uk_libro_sync (sync_key),
            UNIQUE KEY uk_libro_operation (operation_key)
        );
    """, con)) await cmd.ExecuteNonQueryAsync();
    try { await new MySqlCommand("ALTER TABLE libro_caja ADD COLUMN session_id INT NULL AFTER operation_key;", con).ExecuteNonQueryAsync(); } catch { }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS auditoria_contable (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            fecha DATETIME NOT NULL,
            usuario VARCHAR(100) NOT NULL,
            sucursal_id INT NOT NULL,
            accion VARCHAR(80) NOT NULL,
            entidad VARCHAR(80) NOT NULL,
            entidad_id BIGINT NOT NULL,
            detalle TEXT NULL
        );
    """, con)) await cmd.ExecuteNonQueryAsync();
}


static string BuildSaleLineKey(string operationKey, string syncKey, int index, VentaDetalleRequest d)
{
    string raw = string.Join("|",
        string.IsNullOrWhiteSpace(operationKey) ? syncKey : operationKey,
        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        (d.Producto ?? "").Trim().ToUpperInvariant(),
        (d.Presentacion ?? "").Trim().ToUpperInvariant(),
        d.Cantidad.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        d.CantidadBase.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        d.PrecioUnitario.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        d.Subtotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}

static async Task EnsureVentaSyncProtection(MySqlConnection con)
{
    // V41: protege ventas por sync_key. No borra datos existentes automaticamente.
    // Si ya existen duplicados historicos, la migracion V41 incluida debe ejecutarse una sola vez.
    try
    {
        await using var normalize = new MySqlCommand("""
            UPDATE ventas
            SET sync_key = CONCAT('LEGACY-VENTA-', id)
            WHERE sync_key IS NULL OR TRIM(sync_key) = '';
        """, con);
        await normalize.ExecuteNonQueryAsync();
    }
    catch { }

    try
    {
        await using var idx = new MySqlCommand("""
            ALTER TABLE ventas
            ADD UNIQUE KEY uk_ventas_sync_key (sync_key);
        """, con);
        await idx.ExecuteNonQueryAsync();
    }
    catch
    {
        // Si falla por duplicados historicos, la API sigue operativa.
        // Ejecutar MIGRACION_V41_UNIQUE_SYNC_KEY.sql para respaldar y consolidar duplicados.
    }

    // V43: segunda identidad de seguridad. operation_key representa el COBRO de negocio,
    // no el intento de sincronización. Dos PCs no pueden confirmar la misma operación.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN operation_key VARCHAR(220) NULL AFTER sync_key;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD UNIQUE KEY uk_ventas_operation_key (operation_key);", con).ExecuteNonQueryAsync(); } catch { }
    // V48: relación explícita con sesión de mesa y clave de consumo cobrado.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN session_id INT NULL AFTER legacy_fingerprint;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD INDEX idx_ventas_session (sucursal_id, session_id, tipo);", con).ExecuteNonQueryAsync(); } catch { }
    // V56: conservar caja y turno EXACTOS de la venta.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN caja_nombre VARCHAR(60) NULL AFTER cajero;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN turno VARCHAR(20) NULL AFTER caja_nombre;", con).ExecuteNonQueryAsync(); } catch { }

    // V56: backfill histórico. El arqueo no depende de cómo esté configurado hoy el usuario:
    // el turno se deriva de la hora REAL del cobro y la caja se toma del usuario cuando falta.
    try
    {
        await new MySqlCommand("""
            UPDATE ventas v
            LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
            SET v.caja_nombre = CASE
                    WHEN v.caja_nombre IS NOT NULL AND TRIM(v.caja_nombre) <> '' THEN v.caja_nombre
                    WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                    ELSE NULLIF(TRIM(COALESCE(u.caja_nombre,'')), '')
                END,
                v.turno = CASE
                    WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                    ELSE 'NOCHE'
                END
            WHERE v.caja_nombre IS NULL OR TRIM(v.caja_nombre) = ''
               OR v.turno IS NULL OR TRIM(v.turno) = '';
        """, con).ExecuteNonQueryAsync();
    }
    catch { }

    // V44: huella de respaldo para cajas antiguas que no mandan operation_key.
    // NULL para ventas nuevas con OperationKey; hash SHA-256 para solicitudes legacy.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN legacy_fingerprint VARCHAR(64) NULL AFTER operation_key;", con).ExecuteNonQueryAsync(); } catch { }

    // V47: identidad por línea de detalle + libro de movimientos de stock.
    // line_key puede quedar NULL en registros históricos, pero toda venta V128+ lo llena.
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD COLUMN line_key VARCHAR(64) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD UNIQUE KEY uk_detalle_line_key (line_key);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD COLUMN consumption_key VARCHAR(180) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD UNIQUE KEY uk_detalle_consumption_key (consumption_key);", con).ExecuteNonQueryAsync(); } catch { }
    try
    {
        await new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS stock_movimientos_venta (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                movement_key VARCHAR(64) NOT NULL,
                venta_id BIGINT NOT NULL,
                sucursal_id INT NOT NULL,
                producto_id BIGINT NOT NULL,
                presentacion_id BIGINT NULL,
                cantidad_base DECIMAL(12,4) NOT NULL,
                fecha DATETIME NOT NULL,
                creado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY uk_stock_movimiento_key (movement_key),
                KEY ix_stock_mov_venta (venta_id),
                KEY ix_stock_mov_producto (sucursal_id, producto_id, fecha)
            );
        """, con).ExecuteNonQueryAsync();
    }
    catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD UNIQUE KEY uk_ventas_legacy_fingerprint (legacy_fingerprint);", con).ExecuteNonQueryAsync(); } catch { }
}

static string FingerprintNormalize(string? value)
{
    return (value ?? string.Empty).Trim().ToUpperInvariant();
}

static string FingerprintDecimal(decimal value)
{
    return Math.Round(value, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

static string BuildLegacyAccountingFingerprint(VentaRequest venta)
{
    // La precisión se normaliza a segundo para coincidir con copias históricas guardadas en DATETIME.
    DateTime fecha = venta.Fecha;
    string moment = fecha.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    var detalle = new List<string>();
    if (venta.Detalle is not null)
    {
        foreach (var d in venta.Detalle)
        {
            detalle.Add(
                FingerprintNormalize(d.Producto) + "~" +
                FingerprintNormalize(d.Presentacion) + "~" +
                d.Cantidad.ToString(System.Globalization.CultureInfo.InvariantCulture) + "~" +
                FingerprintDecimal(d.Subtotal));
        }
    }
    detalle.Sort(StringComparer.OrdinalIgnoreCase);

    string canonical = string.Join("|", new[]
    {
        venta.SucursalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FingerprintNormalize(venta.Cajero),
        FingerprintNormalize(venta.Tipo),
        FingerprintNormalize(venta.MetodoPago),
        moment,
        FingerprintDecimal(venta.Total),
        FingerprintDecimal(Math.Max(0, venta.Efectivo)),
        FingerprintDecimal(Math.Max(0, venta.Qr)),
        string.Join(";", detalle)
    });

    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static async Task EnsureCoreSchemaAsync(MySqlConnection con)
{
    // V49: tablas base para una base Railway vacía. Todas las sentencias son aditivas.
    string[] statements =
    {
        """
        CREATE TABLE IF NOT EXISTS sucursales (
            id INT AUTO_INCREMENT PRIMARY KEY,
            nombre VARCHAR(120) NOT NULL,
            direccion VARCHAR(220) NOT NULL DEFAULT '',
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO'
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS usuarios (
            id INT AUTO_INCREMENT PRIMARY KEY,
            usuario VARCHAR(100) NOT NULL,
            clave VARCHAR(255) NOT NULL,
            rol VARCHAR(40) NOT NULL,
            sucursal_id INT NOT NULL DEFAULT 1,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            nombre_completo VARCHAR(180) NULL,
            caja_nombre VARCHAR(60) NULL,
            turno VARCHAR(20) NOT NULL DEFAULT 'MAÑANA',
            UNIQUE KEY uk_usuarios_usuario (usuario),
            KEY ix_usuarios_sucursal (sucursal_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS mesas (
            id INT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            nombre VARCHAR(100) NOT NULL,
            precio_hora DECIMAL(12,2) NOT NULL DEFAULT 20.00,
            estado VARCHAR(20) NOT NULL DEFAULT 'LIBRE',
            tipo_mesa VARCHAR(30) NOT NULL DEFAULT 'NORMAL',
            UNIQUE KEY uk_mesas_sucursal_nombre (sucursal_id, nombre),
            KEY ix_mesas_sucursal (sucursal_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS productos (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            nombre VARCHAR(180) NOT NULL,
            categoria VARCHAR(100) NOT NULL DEFAULT 'Otros',
            tipo_entrada VARCHAR(60) NOT NULL DEFAULT 'UNIDAD',
            unidad_base VARCHAR(60) NOT NULL DEFAULT 'UNIDAD',
            unidades_por_entrada INT NOT NULL DEFAULT 1,
            precio_compra DECIMAL(12,2) NOT NULL DEFAULT 0,
            stock_actual DECIMAL(14,4) NOT NULL DEFAULT 0,
            stock_minimo DECIMAL(14,4) NOT NULL DEFAULT 0,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            genera_comision TINYINT(1) NOT NULL DEFAULT 0,
            tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA',
            valor_comision DECIMAL(12,2) NOT NULL DEFAULT 0,
            sin_limite_stock TINYINT(1) NOT NULL DEFAULT 0,
            UNIQUE KEY uk_productos_sucursal_nombre (sucursal_id, nombre),
            KEY ix_productos_sucursal_estado (sucursal_id, estado)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS presentaciones (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            producto_id BIGINT NOT NULL,
            nombre VARCHAR(120) NOT NULL,
            cantidad_base DECIMAL(14,4) NOT NULL DEFAULT 1,
            precio_venta DECIMAL(12,2) NOT NULL DEFAULT 0,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            UNIQUE KEY uk_presentaciones_producto_nombre (producto_id, nombre),
            KEY ix_presentaciones_producto (producto_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ventas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            fecha DATETIME NOT NULL,
            tipo VARCHAR(60) NOT NULL,
            metodo_pago VARCHAR(30) NOT NULL,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            total DECIMAL(12,2) NOT NULL,
            sync_key VARCHAR(220) NOT NULL,
            operation_key VARCHAR(220) NULL,
            legacy_fingerprint VARCHAR(64) NULL,
            session_id INT NULL,
            UNIQUE KEY uk_ventas_sync_key (sync_key),
            UNIQUE KEY uk_ventas_operation_key (operation_key),
            UNIQUE KEY uk_ventas_legacy_fingerprint (legacy_fingerprint),
            KEY ix_ventas_sucursal_fecha (sucursal_id, fecha),
            KEY idx_ventas_session (sucursal_id, session_id, tipo)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS detalle_ventas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            venta_id BIGINT NOT NULL,
            producto_id BIGINT NULL,
            presentacion_id BIGINT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NOT NULL,
            cantidad DECIMAL(14,4) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(12,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(12,2) NOT NULL DEFAULT 0,
            line_key VARCHAR(64) NULL,
            consumption_key VARCHAR(180) NULL,
            UNIQUE KEY uk_detalle_line_key (line_key),
            UNIQUE KEY uk_detalle_consumption_key (consumption_key),
            KEY ix_detalle_venta (venta_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS reservas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            cliente VARCHAR(180) NOT NULL,
            celular VARCHAR(40) NOT NULL DEFAULT '',
            fecha_reserva DATETIME NOT NULL,
            minutos INT NOT NULL DEFAULT 60,
            estado VARCHAR(30) NOT NULL DEFAULT 'ACTIVA',
            cajero VARCHAR(100) NOT NULL DEFAULT '',
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_reservas_sync (sync_key),
            KEY ix_reservas_sucursal_fecha (sucursal_id, fecha_reserva)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS propinas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NULL,
            mesera VARCHAR(150) NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            fecha DATETIME NOT NULL,
            monto DECIMAL(12,2) NOT NULL DEFAULT 0,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_propinas_sync (sync_key),
            KEY ix_propinas_sucursal_fecha (sucursal_id, fecha)
        );
        """
    };

    foreach (string sql in statements)
    {
        await using var cmd = new MySqlCommand(sql, con);
        await cmd.ExecuteNonQueryAsync();
    }

    await using var seed = new MySqlCommand("""
        INSERT IGNORE INTO sucursales (id, nombre, direccion, estado) VALUES
        (1, 'EL BRUJO', '', 'ACTIVO'),
        (2, 'EL BRUJO PREMIU', '', 'ACTIVO');
        UPDATE sucursales SET nombre='EL BRUJO', estado='ACTIVO' WHERE id=1;
        UPDATE sucursales SET nombre='EL BRUJO PREMIU', estado='ACTIVO' WHERE id=2;
    """, con);
    await seed.ExecuteNonQueryAsync();
}

static async Task EnsureUserManagementTables(MySqlConnection con)
{
    await using (var alterClave = new MySqlCommand("ALTER TABLE usuarios MODIFY COLUMN clave VARCHAR(255) NOT NULL;", con))
    {
        try { await alterClave.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN nombre_completo VARCHAR(180) NULL;", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN caja_nombre VARCHAR(60) NULL;", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN turno VARCHAR(20) NOT NULL DEFAULT 'MAÑANA';", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var seed = new MySqlCommand("""
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'admin', 'ElBrujo2026SI', 'ADMINISTRADOR', 1, 'ACTIVO', 'Administrador', 'ADMIN', 'MAÑANA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'admin');

        -- EL BRUJO: 1 PC, 2 cajeros (uno por turno), misma CAJA ÚNICA.
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'brujo_manana', 'BrujoM2026', 'CAJERO', 1, 'ACTIVO', 'Cajero EL BRUJO Mañana', 'CAJA ÚNICA', 'MAÑANA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'brujo_manana');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'brujo_noche', 'BrujoN2026', 'CAJERO', 1, 'ACTIVO', 'Cajero EL BRUJO Noche', 'CAJA ÚNICA', 'NOCHE'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'brujo_noche');

        -- EL BRUJO PREMIU: 2 PCs (ARRIBA / ABAJO), 2 cajeros por turno = 4 cajeros.
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'premiu_arriba_manana', 'PremiuAM2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Arriba Mañana', 'CAJA ARRIBA', 'MAÑANA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu_arriba_manana');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'premiu_abajo_manana', 'PremiuBM2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Abajo Mañana', 'CAJA ABAJO', 'MAÑANA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu_abajo_manana');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'premiu_arriba_noche', 'PremiuAN2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Arriba Noche', 'CAJA ARRIBA', 'NOCHE'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu_arriba_noche');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'premiu_abajo_noche', 'PremiuBN2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Abajo Noche', 'CAJA ABAJO', 'NOCHE'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu_abajo_noche');

        -- Usuarios viejos de caja: se conservan para historial, pero no pueden volver a iniciar sesión.
        UPDATE usuarios
        SET estado = 'INACTIVO'
        WHERE rol = 'CAJERO'
          AND usuario IN ('caja1','caja1_noche','caja2','caja2_noche','caja2_2','caja2_2_noche');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'ana_mesera', 'mesera123', 'MESERA', 1, 'ACTIVO', 'Ana Mesera', '', 'MAÑANA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'ana_mesera');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno)
        SELECT 'rosa_mesera', 'mesera123', 'MESERA', 2, 'ACTIVO', 'Rosa Mesera', '', 'NOCHE'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'rosa_mesera');
    """, con))
    {
        try { await seed.ExecuteNonQueryAsync(); } catch { }
    }

    await HashPlainUserPasswords(con);
}

static string NormalizarRol(string? rol)
{
    string r = (rol ?? "").Trim().ToUpperInvariant();
    if (r.Contains("ADMIN")) return "ADMINISTRADOR";
    if (r.Contains("CAJ")) return "CAJERO";
    if (r.Contains("MESER")) return "MESERA";
    return string.IsNullOrWhiteSpace(r) ? "CAJERO" : r;
}

static string NormalizarTurno(string? turno)
{
    string t = (turno ?? "").Trim().ToUpperInvariant();
    if (t.Contains("NOCHE")) return "NOCHE";
    return "MAÑANA";
}

static string HoraATurno(DateTime fecha)
    => fecha.Hour >= 8 && fecha.Hour < 20 ? "MAÑANA" : "NOCHE";

static string NormalizarCategoriaProducto(string? categoria, string? nombre)
{
    string c = (categoria ?? "").Trim().ToUpperInvariant();
    string n = (nombre ?? "").Trim().ToUpperInvariant();

    if (c.Contains("CENICER") || c.Contains("CINCER") || n.Contains("CENICER") || n.Contains("CINCERO")) return "Ceniceros";
    if (c.Contains("ACCESORIO") || n == "ENCENDEDOR" || n == "COPAS DE VINO" || n == "VASO TEQUILERO" || n == "VASOS CERVECEROS" || n == "VASOS DE SODA" || n == "VASOS DE WISKI" || n == "VASOS DE WHISKY" || n == "VASO DE WISKI" || n == "VASO DE WISKIE" || n == "VASO DE WHISKY") return "Accesorios";
    if (c.Contains("SERVIDOS EN VASO") || c.Contains("LO QUE SE SIRVE EN VASO") || (n.StartsWith("VASO ") && n != "VASO TEQUILERO")) return "Servidos en vaso";
    if (c == "COCAS" || c.Contains("COCA MACHUCADA") || n.StartsWith("COCA EL BRUJO") || n.StartsWith("COCA AMAIRE") || n.StartsWith("COCA BICO") || n.StartsWith("COCA MARACUYA") || n.StartsWith("COCA MEDUSA") || n.StartsWith("COCA RED") || n.StartsWith("COCA SANDIA") || n.StartsWith("COCA YOG")) return "Cocas";
    if (c.Contains("COMBO") || c.Contains("PROMO") || n.Contains("COMBO") || n.StartsWith("PROMO ")) return "Combos / Promos";
    if (c == "AGUA" || n.Contains("AGUA") || n.StartsWith("SANTE ")) return "Agua";
    if (c.Contains("ENERGIZANTE") || n.Contains("RED BULL") || n.Contains("CICLON") || n.Contains("POWER") || n == "BLACK") return "Energizantes";
    if (c.Contains("SODA") || n.StartsWith("SODA ") || n.Contains("SPRITE") || n.Contains("FANTA")) return "Sodas";
    if (c.Contains("CERVEZA") || c == "CERVEZAS" || n.StartsWith("CERVEZA ") || n.Contains("PACEÑA") || n.Contains("CONTI") || n.Contains("CORONA") || n.Contains("SKOL") || n.Contains("SKUL") || n.Contains("AMSTEL")) return "Cervezas";
    if (c.Contains("CIGARRO") || n.Contains("CIGARRO") || n.Contains("CAMEL") || n.Contains("BOHEM") || n.Contains("BOHEN") || n.Contains("HILLS")) return "Cigarros";
    if (c.Contains("SNACK") || c.Contains("PIQUEO") || n.Contains("NACHO") || n.Contains("PAPA") || n.Contains("PIZON") || n.Contains("PINZON") || n.Contains("PLATANITO") || n.Contains("TAKIS") || n.Contains("MIX NAX")) return "Snacks y piqueos";
    if (c.Contains("DULCE") || c.Contains("GOLOSINA") || n.Contains("CHICLE") || n.Contains("CLORETS") || n.Contains("BELDEN") || n.Contains("ARCOR") || n.Contains("HALLS") || n.Contains("MABEL") || n.Contains("GROSO") || n.Contains("MINT") || n.Contains("CHUPETE") || n.Contains("EUCALIPTO") || n.Contains("BICO SABORES")) return "Dulces y golosinas";
    if (c.Contains("TRAGO") || c.Contains("BOTELLA") || n.Contains("RON") || n.Contains("FERNET") || n.Contains("GIN") || n.Contains("TEQUILA") || n.Contains("WHIKY") || n.Contains("WHISK") || n.Contains("WISK") || n.Contains("VINO") || n.Contains("AMARULA") || n.Contains("FLOR DE CAÑA") || n.Contains("FOUR LOCO") || n.Contains("FLOW") || n.Contains("HAVANA") || n.Contains("HABANA") || n.Contains("ICE 51") || n.Contains("NOCHE ICE") || n.Contains("OLD")) return "Tragos / Botellas";
    return "Otros";
}

static async Task<(decimal normal, decimal promoLunes, decimal privada, bool promoActiva)> EnsureTablePricingAsync(MySqlConnection con)
{
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS configuracion_sistema (
            clave VARCHAR(100) PRIMARY KEY,
            valor_decimal DECIMAL(12,2) NULL,
            actualizado DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    // V54: valores iniciales solicitados. El lunes la Caja aplica la promo al INICIAR
    // la sesión y guarda esa tarifa en PricePerHourUsed / tarifa_hora.
    string[] seedSql =
    {
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_LUNES', 10.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PRIVADA', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_ACTIVA', 1.00, NOW());"
    };
    foreach (string sql in seedSql)
    {
        await using var seed = new MySqlCommand(sql, con);
        await seed.ExecuteNonQueryAsync();
    }

    // V54: migración de precio UNA sola vez. Cumple la regla solicitada:
    // lunes Bs 10/h y resto de días Bs 20/h. Después el administrador puede editar
    // normal, promo y privada sin que el reinicio vuelva a sobrescribirlos.
    bool pricingV54Applied = false;
    await using (var checkMigration = new MySqlCommand("SELECT valor_decimal FROM configuracion_sistema WHERE clave='TARIFA_V54_APLICADA' LIMIT 1;", con))
    {
        object? value = await checkMigration.ExecuteScalarAsync();
        pricingV54Applied = value != null && Convert.ToDecimal(value) > 0;
    }
    if (!pricingV54Applied)
    {
        await using var migratePricing = new MySqlCommand("""
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=20.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=20.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_LUNES', 10.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=10.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PRIVADA', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=20.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_ACTIVA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_V54_APLICADA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
        """, con);
        await migratePricing.ExecuteNonQueryAsync();
    }

    decimal normal = 20m, promo = 10m, privada = 20m;
    bool promoActiva = true;
    await using (var get = new MySqlCommand("""
        SELECT clave, valor_decimal
        FROM configuracion_sistema
        WHERE clave IN ('TARIFA_MESA_HORA','TARIFA_MESA_NORMAL','TARIFA_MESA_PROMO_LUNES','TARIFA_MESA_PRIVADA','PROMO_LUNES_ACTIVA');
    """, con))
    await using (var rd = await get.ExecuteReaderAsync())
    {
        decimal? legacy = null;
        while (await rd.ReadAsync())
        {
            string key = rd.IsDBNull(0) ? "" : rd.GetString(0);
            decimal value = rd.IsDBNull(1) ? 0m : rd.GetDecimal(1);
            switch (key)
            {
                case "TARIFA_MESA_HORA": legacy = value; break;
                case "TARIFA_MESA_NORMAL": if (value > 0) normal = value; break;
                case "TARIFA_MESA_PROMO_LUNES": if (value > 0) promo = value; break;
                case "TARIFA_MESA_PRIVADA": if (value > 0) privada = value; break;
                case "PROMO_LUNES_ACTIVA": promoActiva = value > 0; break;
            }
        }
        // Una instalación antigua solo puede tener TARIFA_MESA_HORA. Si NORMAL sigue en su
        // valor semilla pero la tarifa antigua era válida, la conservamos para no sorprender al local.
        if (legacy.HasValue && legacy.Value > 0 && normal == 20m && legacy.Value != 30m)
            normal = legacy.Value;
    }

    if (normal <= 0) normal = 20m;
    if (promo <= 0) promo = 10m;
    if (privada <= 0) privada = 20m;
    normal = Math.Round(normal, 2);
    promo = Math.Round(promo, 2);
    privada = Math.Round(privada, 2);

    // TARIFA_MESA_HORA queda espejada a NORMAL para clientes anteriores.
    await using (var save = new MySqlCommand("""
        INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', @normal, NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
        INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', @normal, NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
    """, con))
    {
        save.Parameters.AddWithValue("@normal", normal);
        await save.ExecuteNonQueryAsync();
    }

    // precio_hora en mesas es precio BASE. Nunca se escribe aquí la promo del lunes,
    // evitando que el martes quede accidentalmente la tarifa promocional.
    await using (var sync = new MySqlCommand("""
        UPDATE mesas
        SET precio_hora = CASE
            WHEN UPPER(TRIM(COALESCE(tipo_mesa,'NORMAL')))='PRIVADA' THEN @privada
            ELSE @normal
        END;
    """, con))
    {
        sync.Parameters.AddWithValue("@normal", normal);
        sync.Parameters.AddWithValue("@privada", privada);
        try { await sync.ExecuteNonQueryAsync(); } catch { }
    }

    return (normal, promo, privada, promoActiva);
}

static async Task<decimal> EnsureGlobalTableRateAsync(MySqlConnection con)
{
    var pricing = await EnsureTablePricingAsync(con);
    return pricing.normal;
}

static async Task EnsureAppMeseraTables(MySqlConnection con)
{
    await using (var alter1 = new MySqlCommand("ALTER TABLE productos ADD COLUMN genera_comision TINYINT(1) NOT NULL DEFAULT 0;", con))
    {
        try { await alter1.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter2 = new MySqlCommand("ALTER TABLE productos ADD COLUMN tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA';", con))
    {
        try { await alter2.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter3 = new MySqlCommand("ALTER TABLE productos ADD COLUMN valor_comision DECIMAL(10,2) NOT NULL DEFAULT 0;", con))
    {
        try { await alter3.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter4 = new MySqlCommand("ALTER TABLE productos ADD COLUMN sin_limite_stock TINYINT(1) NOT NULL DEFAULT 0;", con))
    {
        try { await alter4.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter5 = new MySqlCommand("ALTER TABLE productos ADD COLUMN tipo_entrada VARCHAR(60) NOT NULL DEFAULT 'PAQUETE';", con))
    {
        try { await alter5.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter6 = new MySqlCommand("ALTER TABLE productos ADD COLUMN unidades_por_entrada INT NOT NULL DEFAULT 1;", con))
    {
        try { await alter6.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter7 = new MySqlCommand("ALTER TABLE productos ADD COLUMN precio_compra DECIMAL(10,2) NOT NULL DEFAULT 0;", con))
    {
        try { await alter7.ExecuteNonQueryAsync(); } catch { }
    }

    // Migración de nombres de categoría solicitados para el módulo Productos / Stock.
    await using (var catMig = new MySqlCommand("""
        UPDATE productos SET categoria = 'Cocas' WHERE categoria = 'Coca machucada';
        UPDATE productos SET categoria = 'Otros' WHERE categoria IN ('Varios', 'Otros / Extras');
        UPDATE productos SET categoria = 'Accesorios' WHERE categoria = 'Vasos/Accesorios';
        UPDATE productos SET categoria = 'Ceniceros'
        WHERE UPPER(nombre) LIKE '%CENICER%' OR UPPER(nombre) LIKE '%CINCERO%';
        UPDATE productos SET categoria = 'Accesorios'
        WHERE UPPER(nombre) IN ('ENCENDEDOR','COPAS DE VINO','VASO TEQUILERO','VASOS CERVECEROS','VASOS DE SODA','VASOS DE WISKI','VASOS DE WHISKY','VASO DE WISKI','VASO DE WISKIE','VASO DE WHISKY');
    """, con))
    {
        try { await catMig.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS pedidos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            mesa VARCHAR(100) NOT NULL,
            mesera_usuario VARCHAR(100) NOT NULL,
            mesera_nombre VARCHAR(150) NOT NULL,
            cajero_usuario VARCHAR(100) NULL,
            fecha DATETIME NOT NULL,
            fecha_respuesta DATETIME NULL,
            estado VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE',
            total DECIMAL(10,2) NOT NULL DEFAULT 0,
            observacion VARCHAR(250) NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_pedidos_movil_sync (sync_key),
            INDEX idx_pedidos_movil_sucursal_estado (sucursal_id, estado),
            INDEX idx_pedidos_movil_mesera (mesera_usuario)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS detalle_pedidos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            pedido_id BIGINT NOT NULL,
            producto_id BIGINT NOT NULL,
            presentacion_id BIGINT NOT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NOT NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(10,2) NOT NULL DEFAULT 0,
            genera_comision TINYINT(1) NOT NULL DEFAULT 0,
            tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA',
            valor_comision DECIMAL(10,2) NOT NULL DEFAULT 0,
            comision_calculada DECIMAL(10,2) NOT NULL DEFAULT 0,
            INDEX idx_detalle_pedidos_movil_pedido (pedido_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS comisiones_meseras (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            pedido_id BIGINT NOT NULL,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            mesera_usuario VARCHAR(100) NOT NULL,
            mesera_nombre VARCHAR(150) NOT NULL,
            fecha DATETIME NOT NULL,
            producto VARCHAR(180) NOT NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            venta_total DECIMAL(10,2) NOT NULL DEFAULT 0,
            comision_total DECIMAL(10,2) NOT NULL DEFAULT 0,
            estado VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE_PAGO',
            UNIQUE KEY uk_comision_pedido (pedido_id, producto),
            INDEX idx_comisiones_meseras_fecha (fecha),
            INDEX idx_comisiones_meseras_mesera (mesera_usuario)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS reportes_productos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            turno VARCHAR(30) NOT NULL DEFAULT 'MAÑANA',
            usuario VARCHAR(100) NOT NULL,
            nombre VARCHAR(150) NOT NULL,
            fecha DATETIME NOT NULL,
            producto_id BIGINT NULL,
            presentacion_id BIGINT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            costo_perdido DECIMAL(10,2) NOT NULL DEFAULT 0,
            motivo VARCHAR(80) NOT NULL,
            observacion VARCHAR(250) NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_reporte_producto_sync (sync_key),
            INDEX idx_reporte_producto_fecha (fecha),
            INDEX idx_reporte_producto_sucursal (sucursal_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var seed = new MySqlCommand("""
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado)
        SELECT 'ana_mesera', 'mesera123', 'MESERA', 1, 'ACTIVO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'ana_mesera');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado)
        SELECT 'rosa_mesera', 'mesera123', 'MESERA', 2, 'ACTIVO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'rosa_mesera');
    """, con))
    {
        try { await seed.ExecuteNonQueryAsync(); } catch { }
    }
}


static async Task EnsureShiftCloseTables(MySqlConnection con)
{
    await using var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cierres_turno (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sucursal VARCHAR(120) NOT NULL,
            cajero_usuario VARCHAR(100) NOT NULL,
            cajero_nombre VARCHAR(150) NOT NULL,
            caja VARCHAR(100) NULL,
            turno VARCHAR(30) NOT NULL,
            inicio DATETIME NOT NULL,
            fin DATETIME NOT NULL,
            hora_entrada DATETIME NOT NULL,
            fecha_cierre DATETIME NOT NULL,
            transacciones_total INT NOT NULL DEFAULT 0,
            transacciones_efectivo INT NOT NULL DEFAULT 0,
            transacciones_qr INT NOT NULL DEFAULT 0,
            transacciones_tarjeta INT NOT NULL DEFAULT 0,
            transacciones_transferencia INT NOT NULL DEFAULT 0,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            tarjeta DECIMAL(12,2) NOT NULL DEFAULT 0,
            transferencia DECIMAL(12,2) NOT NULL DEFAULT 0,
            sin_metodo DECIMAL(12,2) NOT NULL DEFAULT 0,
            productos_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            mesas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            minutos_jugados INT NOT NULL DEFAULT 0,
            propinas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            cortesias_valor DECIMAL(12,2) NOT NULL DEFAULT 0,
            comisiones_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            gastos_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            perdidas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            total_generado DECIMAL(12,2) NOT NULL DEFAULT 0,
            neto_turno DECIMAL(12,2) NOT NULL DEFAULT 0,
            observaciones TEXT NULL,
            detalle_json LONGTEXT NOT NULL,
            sync_key VARCHAR(220) NOT NULL,
            UNIQUE KEY uk_cierre_turno_sync (sync_key),
            INDEX idx_cierre_turno_fecha (fecha_cierre),
            INDEX idx_cierre_turno_sucursal (sucursal_id),
            INDEX idx_cierre_turno_cajero (cajero_usuario)
        );
    """, con);
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureOfficialBranchAndTableLayout(MySqlConnection con)
{
    // V49: configuración oficial. No elimina registros históricos ni modifica ventas/inventario.
    try
    {
        await using var rename = new MySqlCommand("""
            UPDATE sucursales SET nombre='EL BRUJO' WHERE id=1;
            UPDATE sucursales SET nombre='EL BRUJO PREMIU' WHERE id=2;
        """, con);
        await rename.ExecuteNonQueryAsync();
    }
    catch { }

    decimal rate = await EnsureGlobalTableRateAsync(con);
    try { await new MySqlCommand("ALTER TABLE mesas ADD COLUMN tipo_mesa VARCHAR(30) NOT NULL DEFAULT 'NORMAL';", con).ExecuteNonQueryAsync(); } catch { }

    foreach (var config in new[] { (SucursalId: 1, Operational: 8), (SucursalId: 2, Operational: 29) })
    {
        for (int i = 1; i <= 29; i++)
        {
            await using var seed = new MySqlCommand("""
                INSERT INTO mesas (sucursal_id, nombre, precio_hora, estado)
                SELECT @sucursal_id, @nombre, @precio, @estado
                WHERE NOT EXISTS (
                    SELECT 1 FROM mesas WHERE sucursal_id=@sucursal_id AND LOWER(TRIM(nombre))=LOWER(TRIM(@nombre))
                );
            """, con);
            seed.Parameters.AddWithValue("@sucursal_id", config.SucursalId);
            seed.Parameters.AddWithValue("@nombre", "Mesa " + i);
            seed.Parameters.AddWithValue("@precio", rate);
            seed.Parameters.AddWithValue("@estado", i <= config.Operational ? "LIBRE" : "INACTIVA");
            try { await seed.ExecuteNonQueryAsync(); } catch { }
        }
    }

    // Mesas operativas oficiales quedan activas si una configuración vieja las dejó inactivas.
    for (int i = 1; i <= 8; i++)
    {
        await using var c = new MySqlCommand("UPDATE mesas SET estado=CASE WHEN UPPER(COALESCE(estado,''))='INACTIVA' THEN 'LIBRE' ELSE estado END WHERE sucursal_id=1 AND nombre=@nombre;", con);
        c.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await c.ExecuteNonQueryAsync(); } catch { }
    }
    for (int i = 1; i <= 29; i++)
    {
        await using var c = new MySqlCommand("UPDATE mesas SET estado=CASE WHEN UPPER(COALESCE(estado,''))='INACTIVA' THEN 'LIBRE' ELSE estado END WHERE sucursal_id=2 AND nombre=@nombre;", con);
        c.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await c.ExecuteNonQueryAsync(); } catch { }
    }

    // EL BRUJO: extras 9..29 se mantienen en la base pero fuera de servicio cuando están libres.
    for (int i = 9; i <= 29; i++)
    {
        await using var off = new MySqlCommand("""
            UPDATE mesas m
            LEFT JOIN mesa_estados me ON me.sucursal_id=m.sucursal_id AND me.mesa_id=m.id
            SET m.estado='INACTIVA'
            WHERE m.sucursal_id=1 AND m.nombre=@nombre
              AND (me.id IS NULL OR UPPER(COALESCE(me.estado,'LIBRE')) IN ('LIBRE','INACTIVA'));
        """, con);
        off.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await off.ExecuteNonQueryAsync(); } catch { }
    }

    try
    {
        await using var types = new MySqlCommand("""
            UPDATE mesas SET tipo_mesa='NORMAL' WHERE sucursal_id=1 AND nombre IN ('Mesa 1','Mesa 2','Mesa 3','Mesa 4','Mesa 5','Mesa 6','Mesa 7');
            UPDATE mesas SET tipo_mesa='PRIVADA' WHERE sucursal_id=1 AND nombre='Mesa 8';
        """, con);
        await types.ExecuteNonQueryAsync();
    }
    catch { }

    // Reaplica precios base después de fijar los tipos oficiales.
    await EnsureTablePricingAsync(con);
}

static async Task EnsureMesasEnVivoTables(MySqlConnection con)
{
    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS mesa_estados (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            mesa VARCHAR(100) NOT NULL,
            estado VARCHAR(50) NOT NULL,
            cajero VARCHAR(100) NULL,
            inicio DATETIME NULL,
            fin_programado DATETIME NULL,
            minutos INT NOT NULL DEFAULT 0,
            tarifa_hora DECIMAL(10,2) NOT NULL DEFAULT 20.00,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_general DECIMAL(10,2) NOT NULL DEFAULT 0,
            cliente_reserva VARCHAR(150) NULL,
            actualizado DATETIME NOT NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_mesa_estado (sucursal_id, mesa_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var alterRate = new MySqlCommand("ALTER TABLE mesa_estados ADD COLUMN tarifa_hora DECIMAL(10,2) NOT NULL DEFAULT 20.00 AFTER minutos;", con))
    {
        try { await alterRate.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS mesa_consumos_vivos (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(10,2) NOT NULL DEFAULT 0,
            mobile_order_id BIGINT NOT NULL DEFAULT 0,
            stock_already_discounted_online TINYINT(1) NOT NULL DEFAULT 0,
            consumption_key VARCHAR(180) NULL,
            actualizado DATETIME NOT NULL,
            INDEX idx_mesa_consumos_vivos (sucursal_id, mesa_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    // V42: una misma mesa se identifica SIEMPRE por sucursal + mesa_id.
    // También se intenta blindar sync_key para que una mesa de otra sucursal nunca pise su estado.
    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD UNIQUE KEY uk_mesa_estado_sucursal_mesa (sucursal_id, mesa_id);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD UNIQUE KEY uk_mesa_estado_sync_key (sync_key);", con).ExecuteNonQueryAsync(); } catch { }

    // Compatibilidad con bases ya creadas en versiones anteriores.
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN mobile_order_id BIGINT NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN stock_already_discounted_online TINYINT(1) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN consumption_key VARCHAR(180) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD INDEX idx_mesa_consumption_key (consumption_key);", con).ExecuteNonQueryAsync(); } catch { }
}

static class PasswordHasher
{
    const int Iterations = 100000;
    const int SaltSize = 16;
    const int KeySize = 32;
    const string Prefix = "PBKDF2$";

    public static bool IsHashed(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Hash(string password)
    {
        password ??= "";
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(KeySize);
        return Prefix + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(key);
    }

    public static bool Verify(string password, string stored)
    {
        password ??= "";
        stored ??= "";

        if (!IsHashed(stored))
            return stored == password;

        string[] parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out int iterations)) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        byte[] actual = pbkdf2.GetBytes(expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}


public sealed class Db
{
    private readonly string _connectionString;

    public Db(IConfiguration configuration)
    {
        _connectionString = BuildConnectionString(configuration);
    }

    public async Task<MySqlConnection> OpenAsync()
    {
        var con = new MySqlConnection(_connectionString);
        await con.OpenAsync();
        return con;
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(
        MySqlConnection con,
        string sql,
        Dictionary<string, object?>? parameters = null)
    {
        await using var cmd = new MySqlCommand(sql, con);

        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
        {
            var item = new Dictionary<string, object?>();
            for (int i = 0; i < rd.FieldCount; i++)
            {
                item[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            }
            rows.Add(item);
        }

        return rows;
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        string? fullUrl = Environment.GetEnvironmentVariable("MYSQL_URL");

        if (!string.IsNullOrWhiteSpace(fullUrl))
        {
            var uri = new Uri(fullUrl);
            string[] mysqlUserInfo = uri.UserInfo.Split(':', 2);
            string mysqlUserFromUrl = Uri.UnescapeDataString(mysqlUserInfo[0]);
            string mysqlPasswordFromUrl = mysqlUserInfo.Length > 1 ? Uri.UnescapeDataString(mysqlUserInfo[1]) : "";
            string mysqlDatabaseFromUrl = uri.AbsolutePath.TrimStart('/');

            return $"Server={uri.Host};Port={uri.Port};Database={mysqlDatabaseFromUrl};Uid={mysqlUserFromUrl};Pwd={mysqlPasswordFromUrl};SslMode=Preferred;";
        }

        string mysqlHost = Environment.GetEnvironmentVariable("MYSQLHOST")
            ?? configuration["MYSQLHOST"]
            ?? "localhost";

        string mysqlPort = Environment.GetEnvironmentVariable("MYSQLPORT")
            ?? configuration["MYSQLPORT"]
            ?? "3306";

        string mysqlDatabaseName = Environment.GetEnvironmentVariable("MYSQLDATABASE")
            ?? Environment.GetEnvironmentVariable("MYSQL_DATABASE")
            ?? configuration["MYSQLDATABASE"]
            ?? configuration["MYSQL_DATABASE"]
            ?? "railway";

        string mysqlUserName = Environment.GetEnvironmentVariable("MYSQLUSER")
            ?? configuration["MYSQLUSER"]
            ?? "root";

        string mysqlPasswordValue = Environment.GetEnvironmentVariable("MYSQLPASSWORD")
            ?? configuration["MYSQLPASSWORD"]
            ?? "";

        return $"Server={mysqlHost};Port={mysqlPort};Database={mysqlDatabaseName};Uid={mysqlUserName};Pwd={mysqlPasswordValue};SslMode=Preferred;";
    }
}


public sealed class SheetsReporter
{
    private readonly string _sheetId;
    private readonly string _credentialsJson;

    public SheetsReporter()
    {
        _sheetId = Environment.GetEnvironmentVariable("GOOGLE_SHEET_ID") ?? "";
        _credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON") ?? "";
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_sheetId) &&
        !string.IsNullOrWhiteSpace(_credentialsJson);

    public string SpreadsheetId => string.IsNullOrWhiteSpace(_sheetId) ? "(sin configurar)" : _sheetId;

    public async Task<string> SyncFromDatabaseAsync(Db db)
    {
        if (!IsConfigured)
            return "Google Sheets no configurado.";

        SheetsService service = CreateService();

        // V53: reportes totalmente separados por sucursal.
        // No existe una hoja financiera consolidada: EL BRUJO y EL BRUJO PREMIU
        // se publican en pestañas distintas para que sus ventas, cierres, inventario
        // y comisiones nunca se mezclen en Google Sheets.
        // Las hojas *_RESUMEN se conservan con fórmulas y NO se sobrescriben.
        await EnsureSheetsAsync(service, new[]
        {
            "EL_BRUJO_CIERRES",
            "EL_BRUJO_VENTAS",
            "EL_BRUJO_PRODUCTOS",
            "EL_BRUJO_INVENTARIO",
            "EL_BRUJO_MESERAS",
            "EL_BRUJO_PREMIU_CIERRES",
            "EL_BRUJO_PREMIU_VENTAS",
            "EL_BRUJO_PREMIU_PRODUCTOS",
            "EL_BRUJO_PREMIU_INVENTARIO",
            "EL_BRUJO_PREMIU_MESERAS"
        });

        await using var con = await db.OpenAsync();

        foreach (int sucursalId in new[] { 1, 2 })
        {
            string prefix = sucursalId == 2 ? "EL_BRUJO_PREMIU" : "EL_BRUJO";
            var args = new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId };

            // Una venta = una fila. La identidad fuerte de ventas (sync_key/operation_key)
            // ya impide que un reintento vuelva a crear el mismo cobro.
            List<List<object>> ventas = new()
            {
                new() { "id_venta", "fecha_turno", "turno", "fecha", "hora", "cajero", "tipo", "metodo_pago", "efectivo", "qr", "total", "caja" }
            };
            ventas.AddRange((await db.QueryAsync(con, """
                SELECT v.id,
                       CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN DATE(v.fecha)
                           WHEN TIME(v.fecha) >= '20:00:00' THEN DATE(v.fecha)
                           ELSE DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                       END AS fecha_turno,
                       COALESCE(NULLIF(v.turno,''), CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                           ELSE 'NOCHE'
                       END) AS turno,
                       DATE(v.fecha) AS fecha, TIME(v.fecha) AS hora,
                       v.cajero, v.tipo, v.metodo_pago,
                       COALESCE(v.efectivo, 0) AS efectivo,
                       COALESCE(v.qr, 0) AS qr,
                       v.total,
                       CASE
                           WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                           WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                           WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                           ELSE COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), 'SIN CAJA')
                       END AS caja
                FROM ventas v
                LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
                WHERE v.sucursal_id = @sucursal_id
                ORDER BY v.fecha DESC, v.id DESC;
            """, args)).Select(r => new List<object>
            {
                Val(r, "id"), DateOnlyText(r, "fecha_turno"), Text(r, "turno"), DateOnlyText(r, "fecha"), Text(r, "hora"),
                Text(r, "cajero"), Text(r, "tipo"), Text(r, "metodo_pago"), Val(r, "efectivo"), Val(r, "qr"), Val(r, "total"), Text(r, "caja")
            }));

            // V53: PRODUCTOS es un resumen diario. El mismo producto/presentación aparece
            // una sola vez por día, aunque se haya vendido en muchas mesas o tickets.
            // Esto evita que la hoja visualmente repita el catálogo y que alguien sume
            // filas duplicadas del reporte. Los importes salen de detalle_ventas de ventas
            // ya protegidas contra reintentos.
            List<List<object>> productos = new()
            {
                new() { "fecha_turno", "turno", "producto", "presentacion", "cantidad_total", "total_vendido", "operaciones" }
            };
            productos.AddRange((await db.QueryAsync(con, """
                SELECT
                       CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN DATE(v.fecha)
                           WHEN TIME(v.fecha) >= '20:00:00' THEN DATE(v.fecha)
                           ELSE DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                       END AS fecha_turno,
                       CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                           ELSE 'NOCHE'
                       END AS turno,
                       MIN(TRIM(d.producto)) AS producto,
                       MIN(TRIM(d.presentacion)) AS presentacion,
                       SUM(d.cantidad) AS cantidad_total,
                       SUM(d.subtotal) AS total_vendido,
                       COUNT(DISTINCT v.id) AS operaciones
                FROM detalle_ventas d
                INNER JOIN ventas v ON v.id = d.venta_id
                WHERE v.sucursal_id = @sucursal_id
                GROUP BY fecha_turno, turno, LOWER(TRIM(d.producto)), LOWER(TRIM(d.presentacion))
                ORDER BY fecha_turno DESC, turno, producto, presentacion;
            """, args)).Select(r => new List<object>
            {
                DateOnlyText(r, "fecha_turno"), Text(r, "turno"), Text(r, "producto"), Text(r, "presentacion"),
                Val(r, "cantidad_total"), Val(r, "total_vendido"), Val(r, "operaciones")
            }));

            // V53: si quedaron productos repetidos de versiones antiguas, el reporte toma
            // solo el registro canónico (menor ID) por sucursal + nombre normalizado.
            // No suma registros duplicados de catálogo, evitando inflar el inventario visible.
            List<List<object>> inventario = new()
            {
                new() { "id_producto", "producto", "categoria", "cantidad_actual", "cantidad_minima", "unidad", "estado" }
            };
            inventario.AddRange((await db.QueryAsync(con, """
                SELECT p.id, p.nombre, p.categoria,
                       GREATEST(p.stock_actual, 0) AS cantidad_actual,
                       p.stock_minimo, p.unidad_base,
                       CASE WHEN p.sin_limite_stock = 1 THEN 'SIN LIMITE'
                            WHEN GREATEST(p.stock_actual, 0) <= p.stock_minimo THEN 'BAJO'
                            ELSE 'OK' END AS estado
                FROM productos p
                INNER JOIN (
                    SELECT sucursal_id, LOWER(TRIM(nombre)) AS nombre_norm, MIN(id) AS id_canonico
                    FROM productos
                    WHERE estado = 'ACTIVO'
                    GROUP BY sucursal_id, LOWER(TRIM(nombre))
                ) canon ON canon.id_canonico = p.id
                WHERE p.sucursal_id = @sucursal_id
                ORDER BY p.nombre;
            """, args)).Select(r => new List<object>
            {
                Val(r, "id"), Text(r, "nombre"), Text(r, "categoria"), Val(r, "cantidad_actual"),
                Val(r, "stock_minimo"), Text(r, "unidad_base"), Text(r, "estado")
            }));

            List<List<object>> meseras = new()
            {
                new()
                {
                    "fecha_turno", "turno", "usuario_mesera", "mesera", "pedidos_con_comision",
                    "cantidad_productos", "total_vendido_generador", "total_comision", "estado"
                }
            };

            if (await TableExistsAsync(con, "comisiones_meseras"))
            {
                meseras.AddRange((await db.QueryAsync(con, """
                    SELECT CASE
                               WHEN TIME(c.fecha) >= '08:00:00' AND TIME(c.fecha) < '20:00:00' THEN DATE(c.fecha)
                               WHEN TIME(c.fecha) >= '20:00:00' THEN DATE(c.fecha)
                               ELSE DATE(DATE_SUB(c.fecha, INTERVAL 1 DAY))
                           END AS fecha_turno,
                           CASE
                               WHEN TIME(c.fecha) >= '08:00:00' AND TIME(c.fecha) < '20:00:00' THEN 'MAÑANA'
                               ELSE 'NOCHE'
                           END AS turno,
                           c.mesera_usuario,
                           COALESCE(NULLIF(c.mesera_nombre, ''), NULLIF(u.nombre_completo, ''), c.mesera_usuario) AS mesera_nombre,
                           COUNT(DISTINCT c.pedido_id) AS pedidos_con_comision,
                           SUM(c.cantidad) AS cantidad_productos,
                           SUM(c.venta_total) AS total_vendido_generador,
                           SUM(c.comision_total) AS total_comision,
                           CASE
                               WHEN SUM(CASE WHEN UPPER(COALESCE(c.estado, 'PENDIENTE_PAGO')) = 'PENDIENTE_PAGO' THEN 1 ELSE 0 END) > 0
                               THEN 'PENDIENTE_PAGO'
                               ELSE 'REGISTRADA'
                           END AS estado
                    FROM comisiones_meseras c
                    LEFT JOIN usuarios u
                           ON u.usuario = c.mesera_usuario
                          AND u.sucursal_id = c.sucursal_id
                    WHERE c.sucursal_id = @sucursal_id
                    GROUP BY fecha_turno, turno, c.mesera_usuario,
                             COALESCE(NULLIF(c.mesera_nombre, ''), NULLIF(u.nombre_completo, ''), c.mesera_usuario)
                    ORDER BY fecha_turno DESC, turno, mesera_nombre;
                """, args)).Select(r => new List<object>
                {
                    DateOnlyText(r, "fecha_turno"), Text(r, "turno"), Text(r, "mesera_usuario"), Text(r, "mesera_nombre"),
                    Val(r, "pedidos_con_comision"), Val(r, "cantidad_productos"),
                    Val(r, "total_vendido_generador"), Val(r, "total_comision"), Text(r, "estado")
                }));
            }

            List<List<object>> cierres = new()
            {
                new()
                {
                    "fecha", "turno", "cajero", "transacciones",
                    "efectivo", "qr", "productos", "mesas", "propinas", "comisiones_meseras",
                    "gastos", "perdidas", "total_generado", "neto_turno", "observaciones", "caja"
                }
            };

            if (await TableExistsAsync(con, "cierres_turno"))
            {
                cierres.AddRange((await db.QueryAsync(con, """
                    SELECT DATE(c.fecha_cierre) AS fecha,
                           c.turno,
                           CASE WHEN COALESCE(c.cajero_nombre, '') <> '' THEN c.cajero_nombre ELSE c.cajero_usuario END AS cajero,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COUNT(*) FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.transacciones_total END AS transacciones_total,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.efectivo),0) FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.efectivo END AS efectivo,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.qr),0) FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.qr END AS qr,
                           c.productos_total, c.mesas_total, c.propinas_total, c.comisiones_total,
                           c.gastos_total, c.perdidas_total,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.total),0) FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.total_generado END AS total_generado,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.total),0) FROM ventas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin) - c.gastos_total
                                ELSE c.neto_turno END AS neto_turno,
                           c.observaciones,
                           CASE
                               WHEN c.sucursal_id = 1 THEN 'CAJA ÚNICA'
                               WHEN UPPER(COALESCE(c.caja, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                               WHEN UPPER(COALESCE(c.caja, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                               ELSE COALESCE(NULLIF(c.caja,''), 'SIN CAJA')
                           END AS caja_reporte
                    FROM cierres_turno c
                    WHERE c.sucursal_id = @sucursal_id
                    ORDER BY c.fecha_cierre DESC, c.id DESC;
                """, args)).Select(r => new List<object>
                {
                    DateOnlyText(r, "fecha"), Text(r, "turno"), Text(r, "cajero"), Val(r, "transacciones_total"),
                    Val(r, "efectivo"), Val(r, "qr"), Val(r, "productos_total"), Val(r, "mesas_total"),
                    Val(r, "propinas_total"), Val(r, "comisiones_total"), Val(r, "gastos_total"), Val(r, "perdidas_total"),
                    Val(r, "total_generado"), Val(r, "neto_turno"), Text(r, "observaciones"), Text(r, "caja_reporte")
                }));
            }

            await ReplaceSheetAsync(service, prefix + "_CIERRES", cierres);
            await ReplaceSheetAsync(service, prefix + "_VENTAS", ventas);
            await ReplaceSheetAsync(service, prefix + "_PRODUCTOS", productos);
            await ReplaceSheetAsync(service, prefix + "_INVENTARIO", inventario);
            await ReplaceSheetAsync(service, prefix + "_MESERAS", meseras);
        }

        return "Google Sheets actualizado por sucursal: EL BRUJO y EL BRUJO PREMIU separados, sin consolidar sus ganancias.";
    }

    private SheetsService CreateService()
    {
        GoogleCredential credential = GoogleCredential
            .FromJson(_credentialsJson)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Billar El Brujo API"
        });
    }

    private async Task EnsureSheetsAsync(SheetsService service, IEnumerable<string> names)
    {
        var spreadsheet = await service.Spreadsheets.Get(_sheetId).ExecuteAsync();
        var existing = spreadsheet.Sheets
            .Select(s => s.Properties.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requests = new List<Google.Apis.Sheets.v4.Data.Request>();

        foreach (string name in names)
        {
            if (!existing.Contains(name))
            {
                requests.Add(new Google.Apis.Sheets.v4.Data.Request
                {
                    AddSheet = new Google.Apis.Sheets.v4.Data.AddSheetRequest
                    {
                        Properties = new Google.Apis.Sheets.v4.Data.SheetProperties
                        {
                            Title = name
                        }
                    }
                });
            }
        }

        if (requests.Count == 0) return;

        var batch = new Google.Apis.Sheets.v4.Data.BatchUpdateSpreadsheetRequest
        {
            Requests = requests
        };

        await service.Spreadsheets.BatchUpdate(batch, _sheetId).ExecuteAsync();
    }

    private async Task ReplaceSheetAsync(SheetsService service, string sheetName, List<List<object>> values)
    {
        string range = "'" + sheetName.Replace("'", "''") + "'!A1:Z5000";

        await service.Spreadsheets.Values.Clear(
            new Google.Apis.Sheets.v4.Data.ClearValuesRequest(),
            _sheetId,
            range
        ).ExecuteAsync();

        var valueRange = new Google.Apis.Sheets.v4.Data.ValueRange
        {
            Values = values.Select(r => (IList<object>)r).ToList()
        };

        var update = service.Spreadsheets.Values.Update(
            valueRange,
            _sheetId,
            "'" + sheetName.Replace("'", "''") + "'!A1"
        );
        update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await update.ExecuteAsync();
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection con, string tableName)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name = @table_name;
        """;

        await using var cmd = new MySqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@table_name", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static object Val(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        return value;
    }

    private static decimal DecimalValue(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return 0m;
        try { return Convert.ToDecimal(value); }
        catch { return 0m; }
    }

    private static string Text(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        return Convert.ToString(value) ?? "";
    }

    private static string DateOnlyText(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
        return Convert.ToString(value) ?? "";
    }
}


public sealed record TableRateRequest(
    decimal PrecioHora = 0m,
    decimal PrecioNormal = 0m,
    decimal PrecioPromoLunes = 10m,
    decimal PrecioPrivada = 20m,
    bool PromoLunesActiva = true
);

public record MesaConsumoVivoRequest(
    string? Producto,
    string? Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Subtotal,
    int MobileOrderId,
    bool StockAlreadyDiscountedOnline,
    string? ConsumptionKey = null
);

public record MesaEstadoRequest(
    int SucursalId,
    int MesaId,
    string? Mesa,
    string? Estado,
    string? Cajero,
    DateTime? Inicio,
    DateTime? FinProgramado,
    int Minutos,
    decimal TarifaHora,
    decimal TotalMesa,
    decimal TotalConsumo,
    decimal TotalGeneral,
    string? ClienteReserva,
    string? SyncKey,
    List<MesaConsumoVivoRequest>? Detalle
);

public record ProductReportRequest(
    int SucursalId,
    string Turno,
    string Usuario,
    string Nombre,
    long ProductoId,
    long PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    string Motivo,
    string Observacion,
    string SyncKey
);

public record ProductCommissionRequest(
    int SucursalId,
    string Nombre,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision
);

public sealed record AdminProductPresentationRequest(
    long? PresentacionId,
    string Nombre,
    int CantidadBase,
    decimal PrecioVenta,
    string Estado
);

public sealed record AdminProductRequest(
    long? ProductoId,
    int SucursalId,
    string Nombre,
    string Categoria,
    string TipoEntrada,
    string UnidadBase,
    int UnidadesPorEntrada,
    decimal PrecioCompra,
    decimal StockActual,
    decimal StockMinimo,
    bool SinLimiteStock,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision,
    string Estado,
    List<AdminProductPresentationRequest>? Presentaciones
);


public sealed record ShiftCloseRequest(
    int SucursalId,
    string? Sucursal,
    string? CajeroUsuario,
    string? CajeroNombre,
    string? Caja,
    string? Turno,
    DateTime Inicio,
    DateTime Fin,
    DateTime HoraEntrada,
    DateTime FechaCierre,
    int TransaccionesTotal,
    int TransaccionesEfectivo,
    int TransaccionesQr,
    int TransaccionesTarjeta,
    int TransaccionesTransferencia,
    decimal Efectivo,
    decimal Qr,
    decimal Tarjeta,
    decimal Transferencia,
    decimal SinMetodo,
    decimal ProductosTotal,
    decimal MesasTotal,
    int MinutosJugados,
    decimal PropinasTotal,
    decimal CortesiasValor,
    decimal ComisionesTotal,
    decimal GastosTotal,
    decimal PerdidasTotal,
    decimal TotalGenerado,
    decimal NetoTurno,
    string? Observaciones,
    string? DetalleJson,
    string? SyncKey
);

public record LoginRequest(string Usuario, string Clave);

public record ProductoRequest(
    int SucursalId,
    string Nombre,
    string Categoria,
    string UnidadBase,
    decimal StockActual,
    decimal StockMinimo
);

public record VentaDetalleRequest(
    int ProductoId,
    int PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal CantidadBase,
    decimal PrecioUnitario,
    decimal Subtotal,
    bool StockAlreadyDiscountedOnline,
    string? ConsumptionKey = null
);

public record VentaRequest(
    int SucursalId,
    string Cajero,
    DateTime Fecha,
    string Tipo,
    string MetodoPago,
    decimal Efectivo,
    decimal Qr,
    decimal Total,
    string? SyncKey,
    string? OperationKey,
    List<VentaDetalleRequest> Detalle,
    int? SessionId = null,
    int? ClientVersion = null,
    string? CajaNombre = null,
    string? Turno = null
);

public record CobroMesaRequest(
    int SucursalId,
    int? SessionId,
    int? MesaId,
    string? Mesa,
    string? Cajero,
    string? Mesera,
    DateTime Fecha,
    string? Tiempo,
    decimal TotalMesa,
    decimal TotalConsumo,
    decimal TotalCobrado,
    string? MetodoPago,
    string? SyncKey
);

public record ReservaRequest(
    int SucursalId,
    int MesaId,
    string Cliente,
    string? Celular,
    DateTime FechaReserva,
    int Minutos,
    string Estado,
    string? Cajero,
    string? SyncKey
);

public record PropinaRequest(
    int SucursalId,
    int? MesaId,
    string Mesera,
    string Cajero,
    DateTime Fecha,
    decimal Monto,
    string? SyncKey
);


public sealed record AppPedidoMovilRequest(
    int SucursalId,
    int MesaId,
    string Mesa,
    string MeseraUsuario,
    string MeseraNombre,
    long ProductoId,
    long PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision,
    string? Observacion,
    string? SyncKey
);

public sealed record PedidoEstadoRequest(
    string Estado,
    string? CajeroUsuario
);


public record AdminUserRequest(
    string Usuario,
    string Clave,
    string NombreCompleto,
    string Rol,
    int SucursalId,
    string CajaNombre,
    string Turno,
    string Estado
);

public record UserEstadoRequest(string Estado);
