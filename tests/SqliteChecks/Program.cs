using Microsoft.Data.Sqlite;

internal static class Program
{
    // 只使用内存数据库：不打开浏览器、官方客户端或本应用的真实数据库。
    static int Main()
    {
        try
        {
            using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT sqlite_version()";
            var version = Version.Parse((string)command.ExecuteScalar()!);
            Assert(version >= new Version(3, 50, 2), $"原生 SQLite 版本为 {version}，低于 CVE-2025-6965 的修补版本 3.50.2。");

            // 与 OfficialTokenReader 读取 cookies 表时的列类型保持一致。
            command.CommandText = "CREATE TABLE cookies (name TEXT, value TEXT, encrypted_value BLOB)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO cookies VALUES ($name, $value, $blob)";
            command.Parameters.AddWithValue("$name", "sample_token");
            command.Parameters.AddWithValue("$value", "sample-value");
            command.Parameters.AddWithValue("$blob", new byte[] { 1, 2, 3 });
            Assert(command.ExecuteNonQuery() == 1, "写入测试数据失败。");

            command.Parameters.Clear();
            command.CommandText = "SELECT name, value, encrypted_value FROM cookies";
            using var reader = command.ExecuteReader();
            Assert(reader.Read(), "读取测试数据失败。");
            Assert(reader.GetString(0) == "sample_token", "TEXT 列读取结果不一致。");
            Assert(reader.GetString(1) == "sample-value", "TEXT 值读取结果不一致。");
            Assert(((byte[])reader.GetValue(2)).SequenceEqual(new byte[] { 1, 2, 3 }), "BLOB 列读取结果不一致。");
            Assert(!reader.Read(), "测试数据行数不为 1。");

            Console.WriteLine($"PASS: 原生 SQLite {version} 已包含修复，cookies 表的 TEXT/BLOB 读取兼容。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
