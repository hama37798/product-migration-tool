using Microsoft.Data.Sqlite;
using System.Text;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime.CredentialManagement;

Console.OutputEncoding = new UTF8Encoding(false);

// --apiを指定したときは、商品一覧APIを起動する
if (args.Contains("--api"))
{
    await ProductApi.RunAsync();
    return;
}

Directory.CreateDirectory("logs");

string logName = $"migration-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt";
string logPath = Path.Combine("logs", logName);
var originalOutput = Console.Out;

// 画面とファイルの両方へ同じ内容を出力する
using (var logFile = new StreamWriter(logPath, false, new UTF8Encoding(false)))
{
    Console.SetOut(new TeeWriter(originalOutput, logFile));

    try
    {
        Console.WriteLine($"処理開始：{DateTimeOffset.Now:O}");
        RunMigration();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"処理中にエラーが発生しました：{ex.Message}");
        Environment.ExitCode = 1;
    }
    finally
    {
        Console.WriteLine($"処理終了：{DateTimeOffset.Now:O}");
        Console.Out.Flush();
        Console.SetOut(originalOutput);
    }
}

Console.WriteLine($"ログ保存先：{Path.GetFullPath(logPath)}");

// 指定したときだけS3へ送る。通常の実行はAWS接続不要
if (args.Contains("--upload-s3"))
{
    const string bucket = "hama37798-product-migration-logs-20261024";
    string key = $"logs/{logName}";

    try
    {
        var profiles = new CredentialProfileStoreChain();

        if (!profiles.TryGetAWSCredentials("product-migration-sdk", out var credentials))
        {
            throw new InvalidOperationException("AWSの認証プロファイルが見つかりません。");
        }

        using var s3 = new AmazonS3Client(
            credentials, RegionEndpoint.APSoutheast2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = logPath,
            ContentType = "text/plain; charset=utf-8"
        }, timeout.Token);

        Console.WriteLine($"S3送信成功：s3://{bucket}/{key}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"S3送信失敗：{ex.Message}");
        Console.WriteLine("ローカルのログは保存済みです。DBの処理結果は変更しません。");
        Environment.ExitCode = 1;
    }
}

// この中に、これまでの商品チェック・登録処理が続く
void RunMigration()
{

// ① DBに接続する。ファイルがなければ作成される
using var connection = new SqliteConnection("Data Source=products.db");
connection.Open();

// ② 商品テーブルを作る
using var createCommand = connection.CreateCommand();
createCommand.CommandText = """
    CREATE TABLE IF NOT EXISTS products (
        product_code TEXT NOT NULL PRIMARY KEY,
        product_name TEXT NOT NULL,
        unit_price INTEGER NOT NULL CHECK (unit_price >= 0),
        stock_quantity INTEGER NOT NULL CHECK (stock_quantity >= 0)
    );
    """;
createCommand.ExecuteNonQuery();

// ③ サンプル商品を登録する
// 同じ商品コードが登録済みなら、その商品は追加しない
using var insertCommand = connection.CreateCommand();
insertCommand.CommandText = """
    INSERT INTO products
        (product_code, product_name, unit_price, stock_quantity)
    VALUES
        ('G001', '透明ガラス', 3000, 10),
        ('G002', 'すりガラス', 4500, 5),
        ('S001', 'アルミサッシ', 12000, 3)
    ON CONFLICT(product_code) DO NOTHING;
    """;
insertCommand.ExecuteNonQuery();

// ④ SQLで商品一覧と「単価×在庫数量」を取得する
using var selectCommand = connection.CreateCommand();
selectCommand.CommandText = """
    SELECT
        product_code,
        product_name,
        unit_price,
        stock_quantity,
        unit_price * stock_quantity AS stock_amount
    FROM products
    WHERE stock_quantity <= 5
    ORDER BY product_code;
    """;

// ⑤ 取得した結果を1行ずつ表示する
using var reader = selectCommand.ExecuteReader();

Console.WriteLine("商品コード | 商品名 | 単価 | 在庫数量 | 単価×数量");

while (reader.Read())
{
    var code = reader.GetString(0);
    var name = reader.GetString(1);
    var price = reader.GetInt64(2);
    var quantity = reader.GetInt64(3);
    var amount = reader.GetInt64(4);

    Console.WriteLine(
        $"{code} | {name} | {price}円 | {quantity} | {amount}円");
}

reader.Close();

// ⑥ CSVを読み込んでチェックする
Console.WriteLine();
Console.WriteLine("=== CSVの商品データチェック ===");

string csvPath = "products.csv";

if (!File.Exists(csvPath))
{
    Console.WriteLine($"ファイルが見つかりません：{csvPath}");
    return;
}

string[] lines = File.ReadAllLines(csvPath);

int validCount = 0;
int errorCount = 0;

// CSV内に同じ商品コードがないか確認する
var seenCodes = new HashSet<string>(StringComparer.Ordinal);

// 全件のチェックが終わるまで、商品をメモリ上に保管する
var pendingProducts =
    new List<(string Code, string Name, int Price, int Quantity)>();

// 先頭行は見出しなので、2行目（添字1）から処理する
for (int i = 1; i < lines.Length; i++)
{
    string[] columns = lines[i].Split(',');
    int lineNumber = i + 1;

    if (columns.Length != 4)
    {
        Console.WriteLine(
            $"{lineNumber}行目：項目数が違います。4項目にしてください。");
        errorCount++;
        continue;
    }

    string code = columns[0].Trim();
    string name = columns[1].Trim();

    // この行で見つかったエラーをためる
    var errors = new List<string>();

    if (!seenCodes.Add(code))
    {
        errors.Add($"商品コード「{code}」がCSV内で重複しています");
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        errors.Add("商品コードを入力してください");
    }

    if (string.IsNullOrWhiteSpace(name))
    {
        errors.Add("商品名を入力してください");
    }

    if (!int.TryParse(columns[2], out int price) || price < 0)
    {
        errors.Add("単価は0以上の整数で入力してください");
    }

    if (!int.TryParse(columns[3], out int quantity) || quantity < 0)
    {
        errors.Add("在庫数量は0以上の整数で入力してください");
    }

    // 登録済みの商品コードか、SQLで確認する
    using var checkCommand = connection.CreateCommand();

    checkCommand.CommandText = """
        SELECT COUNT(*)
        FROM products
        WHERE product_code = $code;
        """;

    checkCommand.Parameters.AddWithValue("$code", code);

    long existingCount = (long)checkCommand.ExecuteScalar()!;

    if (existingCount > 0)
    {
        errors.Add($"商品コード「{code}」はDBに登録済みです");
    }

    if (errors.Count > 0)
    {
        Console.WriteLine(
            $"{lineNumber}行目：{string.Join("／", errors)}");
        errorCount++;
        continue;
    }

        Console.WriteLine($"OK：{code} | {name} | {price}円 | {quantity}");

        pendingProducts.Add((code, name, price, quantity));
        validCount++;
}

Console.WriteLine();
Console.WriteLine($"正常：{validCount}件／エラー：{errorCount}件");

// 1件でもエラーがあれば、正常な行も含めて登録しない
if (errorCount > 0)
{
    Console.WriteLine("エラーがあるため、CSVの商品は1件も登録しませんでした。");
    return;
}

if (pendingProducts.Count == 0)
{
    Console.WriteLine("登録対象の商品がありません。");
    return;
}

// 複数商品の登録を、ひとまとまりの処理として扱う
using var transaction = connection.BeginTransaction();

try
{
    foreach (var product in pendingProducts)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO products
                (product_code, product_name, unit_price, stock_quantity)
            VALUES
                ($code, $name, $price, $quantity);
            """;

        command.Parameters.AddWithValue("$code", product.Code);
        command.Parameters.AddWithValue("$name", product.Name);
        command.Parameters.AddWithValue("$price", product.Price);
        command.Parameters.AddWithValue("$quantity", product.Quantity);

        command.ExecuteNonQuery();
    }

    // 全件成功したので確定する
    transaction.Commit();

    Console.WriteLine($"{pendingProducts.Count}件の商品をDBに登録しました。");
}
catch (SqliteException ex)
{
    // 途中で失敗した場合、それまでの登録も取り消す
    transaction.Rollback();

    Console.WriteLine("登録に失敗したため、今回の登録をすべて取り消しました。");
    Console.WriteLine($"原因：{ex.Message}");
}

// 登録後のDB全体の件数を確認する
using var countCommand = connection.CreateCommand();
countCommand.CommandText = "SELECT COUNT(*) FROM products;";

Console.WriteLine($"DBの商品総数：{countCommand.ExecuteScalar()}件");

} // RunMigrationの終わり

// Console.WriteLineの出力を、画面とログへ振り分ける
sealed class TeeWriter(TextWriter screen, TextWriter file) : TextWriter
{
    public override Encoding Encoding => screen.Encoding;

    public override void Write(char value)
    {
        screen.Write(value);
        file.Write(value);
    }

    public override void Write(string? value)
    {
        screen.Write(value);
        file.Write(value);
    }

    public override void Flush()
    {
        screen.Flush();
        file.Flush();
    }
}