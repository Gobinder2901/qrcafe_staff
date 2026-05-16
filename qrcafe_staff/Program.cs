using MySql.Data.MySqlClient;

var builder = WebApplication.CreateBuilder(args);

// ======================================================
// CORS
// ======================================================

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("AllowAll");

// ======================================================
// CONNECTION STRING
// ======================================================

string connStr =
    builder.Configuration.GetConnectionString("MySql")!;

// ======================================================
// HEALTH CHECK
// ======================================================

app.MapGet("/api/health", () =>
{
    return Results.Ok(new
    {
        success = true,
        message = "QRCAFE STAFF POS API RUNNING",
        time = DateTime.Now
    });
});

// ======================================================
// LIVE DASHBOARD
// ======================================================

app.MapGet("/api/dashboard/live/{branchId}", async (
    int branchId) =>
{
    var list = new List<object>();

    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    string sql = @"
    SELECT
        o.id,
        o.table_id,
        o.status,
        o.payment_status,
        o.total_amount,
        o.special_instructions,
        o.created_at,
        rt.table_number
    FROM orders o
    LEFT JOIN restaurant_tables rt
        ON rt.id = o.table_id
    WHERE o.branch_id = @branchId
    AND o.is_hidden_from_table = 0
    ORDER BY o.created_at DESC";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    while (await rdr.ReadAsync())
    {
        list.Add(new
        {
            id = rdr["id"],

            orderNumber =
        "ORD-" + rdr["id"]?.ToString(),

            tableNo = rdr["table_number"]?.ToString(),

            status = rdr["status"]?.ToString(),

            paymentStatus =
        rdr["payment_status"]?.ToString(),

            total = rdr["total_amount"],

            instructions =
        rdr["special_instructions"]?.ToString(),

            createdAt = rdr["created_at"]
        });
    }

    return Results.Ok(list);
});

// ======================================================
// DAILY REPORTS
// ======================================================

app.MapGet("/api/reports/daily/{branchId}", async (
    int branchId) =>
{
    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    string sql = @"
    SELECT
        COUNT(*) total_orders,
        IFNULL(SUM(total_amount),0) total_sales,
        IFNULL(SUM(tax_amount),0) total_tax,
        IFNULL(SUM(service_charge),0) total_service
    FROM orders
    WHERE branch_id = @branchId";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    if (await rdr.ReadAsync())
    {
        return Results.Ok(new
        {
            totalOrders =
                Convert.ToInt32(rdr["total_orders"]),

            totalSales =
                Convert.ToDecimal(rdr["total_sales"]),

            totalTax =
                Convert.ToDecimal(rdr["total_tax"]),

            totalService =
                Convert.ToDecimal(rdr["total_service"])
        });
    }

    return Results.Ok(new
    {
        totalOrders = 0,
        totalSales = 0,
        totalTax = 0,
        totalService = 0
    });
});

// ======================================================
// TABLE MANAGEMENT
// ======================================================

app.MapGet("/api/tables/{branchId}", async (
    int branchId) =>
{
    var list = new List<object>();

    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    string sql = @"
    SELECT
        id,
        table_number,
        capacity,
        status
    FROM restaurant_tables
    WHERE branch_id = @branchId
    ORDER BY table_number";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    while (await rdr.ReadAsync())
    {
        list.Add(new
        {
            id = rdr["id"],
            tableNo = rdr["table_number"],
            capacity = rdr["capacity"],
            status = rdr["status"]
        });
    }

    return Results.Ok(list);
});

// ======================================================
// CREATE POS ORDER
// ======================================================

app.MapPost("/api/orders/create", async (
    CreateOrderRequest req) =>
{
    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    using var tran = await con.BeginTransactionAsync();

    try
    {
        decimal subtotal =
            req.Items.Sum(x => x.Price * x.Quantity);

        decimal tax = subtotal * 0.05m;

        decimal grandTotal =
            subtotal + tax + req.ServiceCharge;

        string insertOrder = @"
        INSERT INTO orders
        (
            restaurant_id,
            branch_id,
            table_id,
            status,
            payment_status,
            total_amount,
            special_instructions,
            subtotal,
            tax_amount,
            service_charge,
            created_at
        )
        VALUES
        (
            @restaurant_id,
            @branch_id,
            @table_id,
            'awaiting',
            'unpaid',
            @total_amount,
            @special_instructions,
            @subtotal,
            @tax_amount,
            @service_charge,
            NOW()
        );

        SELECT LAST_INSERT_ID();";

        long orderId;

        using (var cmd = new MySqlCommand(
            insertOrder,
            con,
            (MySqlTransaction)tran))
        {
            cmd.Parameters.AddWithValue(
                "@restaurant_id",
                req.RestaurantId);

            cmd.Parameters.AddWithValue(
                "@branch_id",
                req.BranchId);

            cmd.Parameters.AddWithValue(
                "@table_id",
                req.TableId);

            cmd.Parameters.AddWithValue(
                "@total_amount",
                grandTotal);

            cmd.Parameters.AddWithValue(
                "@special_instructions",
                req.SpecialInstructions ?? "");

            cmd.Parameters.AddWithValue(
                "@subtotal",
                subtotal);

            cmd.Parameters.AddWithValue(
                "@tax_amount",
                tax);

            cmd.Parameters.AddWithValue(
                "@service_charge",
                req.ServiceCharge);

            orderId =
                Convert.ToInt64(
                    await cmd.ExecuteScalarAsync());
        }

        foreach (var item in req.Items)
        {
            string itemSql = @"
            INSERT INTO order_items
            (
                order_id,
                item_id,
                quantity,
                price
            )
            VALUES
            (
                @order_id,
                @item_id,
                @quantity,
                @price
            )";

            using var itemCmd =
                new MySqlCommand(
                    itemSql,
                    con,
                    (MySqlTransaction)tran);

            itemCmd.Parameters.AddWithValue(
                "@order_id",
                orderId);

            itemCmd.Parameters.AddWithValue(
                "@item_id",
                item.ItemId);

            itemCmd.Parameters.AddWithValue(
                "@quantity",
                item.Quantity);

            itemCmd.Parameters.AddWithValue(
                "@price",
                item.Price);

            await itemCmd.ExecuteNonQueryAsync();
        }

        string updateTable = @"
        UPDATE restaurant_tables
        SET status = 'awaiting'
        WHERE id = @tableId";

        using (var tableCmd =
            new MySqlCommand(
                updateTable,
                con,
                (MySqlTransaction)tran))
        {
            tableCmd.Parameters.AddWithValue(
                "@tableId",
                req.TableId);

            await tableCmd.ExecuteNonQueryAsync();
        }

        await tran.CommitAsync();

        return Results.Ok(new
        {
            success = true,
            orderId
        });
    }
    catch (Exception ex)
    {
        await tran.RollbackAsync();

        return Results.BadRequest(new
        {
            success = false,
            error = ex.Message
        });
    }
});

// ======================================================
// UPDATE ORDER STATUS
// ======================================================

app.MapPut("/api/orders/status", async (
    UpdateOrderStatusRequest req) =>
{
    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    string sql = @"
    UPDATE orders
    SET status = @status
    WHERE id = @orderId";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue(
        "@status",
        req.Status);

    cmd.Parameters.AddWithValue(
        "@orderId",
        req.OrderId);

    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        success = true
    });
});

// ======================================================
// UPDATE PAYMENT STATUS
// ======================================================

app.MapPut("/api/orders/payment", async (
    UpdatePaymentRequest req) =>
{
    using var con = new MySqlConnection(connStr);

    await con.OpenAsync();

    string sql = @"
    UPDATE orders
    SET payment_status = @payment_status
    WHERE id = @orderId";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue(
        "@payment_status",
        req.PaymentStatus);

    cmd.Parameters.AddWithValue(
        "@orderId",
        req.OrderId);

    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        success = true
    });
});

app.Run();

// ======================================================
// DTOS
// ======================================================

record CreateOrderRequest(
    int RestaurantId,
    int BranchId,
    int TableId,
    string? SpecialInstructions,
    decimal ServiceCharge,
    List<CreateOrderItem> Items
);

record CreateOrderItem(
    int ItemId,
    int Quantity,
    decimal Price
);

record UpdateOrderStatusRequest(
    int OrderId,
    string Status
);

record UpdatePaymentRequest(
    int OrderId,
    string PaymentStatus
);