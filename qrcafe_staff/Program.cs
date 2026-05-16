using MySql.Data.MySqlClient;
using System.Data;

var builder = WebApplication.CreateBuilder(args);



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

string connStr = builder.Configuration.GetConnectionString("MySql")!;

// ============================================
// HEALTH
// ============================================

app.MapGet("/api/health", () =>
{
    return Results.Ok(new
    {
        status = "POS API RUNNING",
        time = DateTime.Now
    });
});

// ============================================
// LIVE DASHBOARD
// ============================================

app.MapGet("/api/dashboard/live/{branchId}", async (int branchId) =>
{
    using var con = new MySqlConnection(connStr);
    await con.OpenAsync();

    string sql = @"
    SELECT
        o.id,
        o.order_number,
        rt.table_no,
        o.order_status,
        o.payment_status,
        TIMESTAMPDIFF(SECOND,o.created_at,NOW()) AS timer_seconds,
        o.grand_total,
        o.created_at
    FROM pos_orders o
    LEFT JOIN restaurant_tables rt ON rt.id=o.table_id
    WHERE o.branch_id=@branchId
    AND o.order_status<>'PAID'
    ORDER BY o.created_at ASC";

    using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    var list = new List<object>();

    while (await rdr.ReadAsync())
    {
        list.Add(new
        {
            id = rdr["id"],
            orderNumber = rdr["order_number"],
            tableNo = rdr["table_no"],
            status = rdr["order_status"],
            paymentStatus = rdr["payment_status"],
            timer = rdr["timer_seconds"],
            total = rdr["grand_total"]
        });
    }

    return Results.Ok(list);
});

// ============================================
// TABLE MANAGEMENT
// ============================================

app.MapGet("/api/tables/{branchId}", async (int branchId) =>
{
    using var con = new MySqlConnection(connStr);
    await con.OpenAsync();

    string sql = @"
    SELECT *
    FROM restaurant_tables
    WHERE branch_id=@branchId
    ORDER BY table_no";

    using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    var list = new List<object>();

    while (await rdr.ReadAsync())
    {
        list.Add(new
        {
            id = rdr["id"],
            tableNo = rdr["table_no"],
            status = rdr["status"],
            capacity = rdr["capacity"]
        });
    }

    return Results.Ok(list);
});

// ============================================
// CREATE ORDER
// ============================================

app.MapPost("/api/orders/create", async (CreateOrderRequest req) =>
{
    using var con = new MySqlConnection(connStr);
    await con.OpenAsync();

    using var tran = await con.BeginTransactionAsync();

    try
    {
        string orderNo = "ORD-" + DateTime.Now.ToString("yyyyMMddHHmmss");

        decimal subtotal = req.Items.Sum(x => x.Price * x.Quantity);
        decimal gst = subtotal * 0.05m;
        decimal grandTotal = subtotal + gst;

        string insertOrder = @"
        INSERT INTO pos_orders
        (
            restaurant_id,
            branch_id,
            table_id,
            order_number,
            order_source,
            customer_name,
            waiter_id,
            subtotal,
            gst_amount,
            grand_total,
            notes
        )
        VALUES
        (
            @restaurant_id,
            @branch_id,
            @table_id,
            @order_number,
            @order_source,
            @customer_name,
            @waiter_id,
            @subtotal,
            @gst_amount,
            @grand_total,
            @notes
        );

        SELECT LAST_INSERT_ID();";

        long orderId;

        using (var cmd = new MySqlCommand(insertOrder, con, (MySqlTransaction)tran))
        {
            cmd.Parameters.AddWithValue("@restaurant_id", req.RestaurantId);
            cmd.Parameters.AddWithValue("@branch_id", req.BranchId);
            cmd.Parameters.AddWithValue("@table_id", req.TableId);
            cmd.Parameters.AddWithValue("@order_number", orderNo);
            cmd.Parameters.AddWithValue("@order_source", req.OrderSource);
            cmd.Parameters.AddWithValue("@customer_name", req.CustomerName ?? "Walk In");
            cmd.Parameters.AddWithValue("@waiter_id", req.WaiterId);
            cmd.Parameters.AddWithValue("@subtotal", subtotal);
            cmd.Parameters.AddWithValue("@gst_amount", gst);
            cmd.Parameters.AddWithValue("@grand_total", grandTotal);
            cmd.Parameters.AddWithValue("@notes", req.Notes ?? "");

            orderId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        foreach (var item in req.Items)
        {
            string itemSql = @"
            INSERT INTO pos_order_items
            (
                order_id,
                menu_item_id,
                item_name,
                quantity,
                price,
                total,
                special_instruction
            )
            VALUES
            (
                @order_id,
                @menu_item_id,
                @item_name,
                @quantity,
                @price,
                @total,
                @special_instruction
            )";

            using var itemCmd = new MySqlCommand(itemSql, con, (MySqlTransaction)tran);

            itemCmd.Parameters.AddWithValue("@order_id", orderId);
            itemCmd.Parameters.AddWithValue("@menu_item_id", item.MenuItemId);
            itemCmd.Parameters.AddWithValue("@item_name", item.ItemName);
            itemCmd.Parameters.AddWithValue("@quantity", item.Quantity);
            itemCmd.Parameters.AddWithValue("@price", item.Price);
            itemCmd.Parameters.AddWithValue("@total", item.Price * item.Quantity);
            itemCmd.Parameters.AddWithValue("@special_instruction", item.SpecialInstruction ?? "");

            await itemCmd.ExecuteNonQueryAsync();
        }

        string tableUpdate = @"
        UPDATE restaurant_tables
        SET status='AWAITING'
        WHERE id=@table_id";

        using (var tableCmd = new MySqlCommand(tableUpdate, con, (MySqlTransaction)tran))
        {
            tableCmd.Parameters.AddWithValue("@table_id", req.TableId);
            await tableCmd.ExecuteNonQueryAsync();
        }

        await tran.CommitAsync();

        return Results.Ok(new
        {
            success = true,
            orderId,
            orderNo
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

// ============================================
// UPDATE ORDER STATUS
// ============================================

app.MapPut("/api/orders/status", async (UpdateOrderStatusRequest req) =>
{
    using var con = new MySqlConnection(connStr);
    await con.OpenAsync();

    string sql = @"
    UPDATE pos_orders
    SET order_status=@status
    WHERE id=@order_id";

    using var cmd = new MySqlCommand(sql, con);

    cmd.Parameters.AddWithValue("@status", req.Status);
    cmd.Parameters.AddWithValue("@order_id", req.OrderId);

    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        success = true
    });
});

// ============================================
// SALES REPORT
// ============================================

app.MapGet("/api/reports/daily/{branchId}", async (int branchId) =>
{
    using var con = new MySqlConnection(connStr);
    await con.OpenAsync();

    string sql = @"
    SELECT
        COUNT(*) AS total_orders,
        SUM(grand_total) AS total_sales,
        SUM(gst_amount) AS total_tax
    FROM pos_orders
    WHERE branch_id=@branchId
    AND DATE(created_at)=CURDATE()";

    using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@branchId", branchId);

    using var rdr = await cmd.ExecuteReaderAsync();

    if (await rdr.ReadAsync())
    {
        return Results.Ok(new
        {
            totalOrders = rdr["total_orders"],
            totalSales = rdr["total_sales"],
            totalTax = rdr["total_tax"]
        });
    }

    return Results.Ok();
});





app.UseHttpsRedirection();

app.Run();

// ============================================
// DTO MODELS
// ============================================

record CreateOrderRequest(
    int RestaurantId,
    int BranchId,
    int TableId,
    string OrderSource,
    string? CustomerName,
    int WaiterId,
    string? Notes,
    List<CreateOrderItem> Items
);

record CreateOrderItem(
    int MenuItemId,
    string ItemName,
    decimal Quantity,
    decimal Price,
    string? SpecialInstruction
);

record UpdateOrderStatusRequest(
    long OrderId,
    string Status
);
