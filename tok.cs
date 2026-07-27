#:package Microsoft.ML.Tokenizers@0.22.0
#:package Microsoft.ML.Tokenizers.Data.O200kBase@0.22.0
#:package Microsoft.Data.Sqlite@9.0.0
// Token meter for the AITM store: sums the real BPE token cost of stored content so storage is
// optimized for TOKENS, not chars/bytes. o200k_base (GPT-4o/o-series BPE) is a close proxy for
// what an LLM context actually pays. Usage: dotnet run tok.cs [instance]   (default "nomercy")
using Microsoft.Data.Sqlite;
using Microsoft.ML.Tokenizers;

string instance = args.Length > 0 ? args[0] : "nomercy";
string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aitm", instance, "aitm.db");
Tokenizer tok = TiktokenTokenizer.CreateForModel("gpt-4o");

using SqliteConnection con = new($"Data Source={path};Mode=ReadOnly");
con.Open();

long Sum(string table, string column)
{
    long total = 0;
    using SqliteCommand cmd = con.CreateCommand();
    cmd.CommandText = $"SELECT {column} FROM {table}";
    using SqliteDataReader r = cmd.ExecuteReader();
    while (r.Read())
        if (!r.IsDBNull(0)) total += tok.CountTokens(r.GetString(0));
    return total;
}

long docTokens = Sum("docs", "content");
long factTokens = Sum("facts", "value");
long docCount = (long)(new SqliteCommand("SELECT count(*) FROM docs", con).ExecuteScalar() ?? 0L);

Console.WriteLine($"instance '{instance}' token cost (o200k_base):");
Console.WriteLine($"  docs:  {docTokens,8} tok across {docCount} sections  (avg {(docCount == 0 ? 0 : docTokens / docCount)}/section)");
Console.WriteLine($"  facts: {factTokens,8} tok");
