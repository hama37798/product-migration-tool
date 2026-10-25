using Microsoft.Data.Sqlite;

static class ProductApi
{
    public static async Task RunAsync()
    {
        string dbPath = Path.GetFullPath("products.db");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine("products.dbが見つかりません。");
            Console.WriteLine("プロジェクトのフォルダーで先に dotnet run を実行してください。");
            Environment.ExitCode = 1;
            return;
        }

        // APIでは既存DBを読み取り専用で開く
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapGet("/", () => Results.Ok(new
        {
            application = "Product Migration Tool",
            productsUrl = "/products"
        }));

        app.MapGet("/products", () =>
        {
            // リクエストごとに接続を作成し、終了時に閉じる
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    product_code,
                    product_name,
                    unit_price,
                    stock_quantity,
                    unit_price * stock_quantity AS stock_amount
                FROM products
                ORDER BY product_code;
                """;

            using var reader = command.ExecuteReader();
            var products = new List<ProductResponse>();

            while (reader.Read())
            {
                products.Add(new ProductResponse(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)
                ));
            }

            return Results.Ok(new
            {
                count = products.Count,
                products
            });
        });

        Console.WriteLine($"参照DB：{dbPath}");
        Console.WriteLine("商品一覧：http://localhost:5050/products");
        Console.WriteLine("停止するときは Ctrl+C を押してください。");

        // このPCから利用する学習用API
        await app.RunAsync("http://localhost:5050");
    }

    private sealed record ProductResponse(
        string ProductCode,
        string ProductName,
        long UnitPrice,
        long StockQuantity,
        long StockAmount);
}