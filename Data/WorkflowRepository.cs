using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using PQA.Web.Models;
using PQA.Web.Services;

namespace PQA.Web.Data;

public sealed class WorkflowRepository
{
    private readonly SecureSettingsProvider settings;
    public WorkflowRepository(SecureSettingsProvider settings) => this.settings = settings;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(settings.Get("MDTE"));
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT 1", cn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<UserSession?> AuthenticateAsync(string password, CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT TOP (1) [人名],[部門],[是否主管] FROM [PASS] WHERE LOWER(RTRIM([E_PASS]))=@password", cn);
        cmd.Parameters.Add("@password", SqlDbType.NVarChar, 20).Value = password.Trim().ToLowerInvariant();
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new UserSession
        {
            Password = password.Trim().ToLowerInvariant(),
            PersonName = Text(reader["人名"]),
            Department = Text(reader["部門"]),
            IsSupervisor = Text(reader["是否主管"]).Equals("Y", StringComparison.OrdinalIgnoreCase)
        };
    }

    public async Task<IReadOnlyList<string>> LookupAsync(string type, CancellationToken ct)
    {
        string sql = type switch
        {
            "employees" => "SELECT DISTINCT RTRIM([人員]) FROM [執行人員] WHERE RTRIM([人員])<>'' ORDER BY 1",
            "locations" => "SELECT DISTINCT RTRIM([L_LOC]) FROM [LOC] WHERE RTRIM([L_LOC])<>'' ORDER BY 1",
            _ => throw new KeyNotFoundException("無效的選項清單。")
        };
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        var result = new List<string>();
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Text(reader[0]));
        return result;
    }

    public async Task<IReadOnlyList<Dictionary<string, string?>>> SearchAsync(WorkflowDefinition definition, string? query, bool unfinishedOnly, CancellationToken ct)
    {
        string q = (query ?? "").Trim();
        string selectFields = string.Join(',', definition.SearchFields.Select(Quote));
        string where = BuildSearchWhere(definition.SearchFields, !string.IsNullOrWhiteSpace(q), unfinishedOnly, definition.CompletionField);
        string sql;
        if (definition.IsLineBased)
        {
            string aggregates = string.Join(',', definition.SearchFields.Skip(1).Select(x => $"MAX({Quote(x)}) AS {Quote(x)}"));
            sql = $"SELECT TOP (100) {Quote(WorkflowDefinition.KeyColumn)},{aggregates} FROM {Quote(definition.MasterTable)} {where} GROUP BY {Quote(WorkflowDefinition.KeyColumn)} ORDER BY MAX({Quote("收文日期")}) DESC,{Quote(WorkflowDefinition.KeyColumn)} DESC";
        }
        else
        {
            sql = $"SELECT TOP (100) {selectFields} FROM {Quote(definition.MasterTable)} {where} ORDER BY {Quote(WorkflowDefinition.KeyColumn)} DESC";
        }

        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        if (!string.IsNullOrWhiteSpace(q))
            cmd.Parameters.Add("@query", SqlDbType.NVarChar, 120).Value = $"%{q}%";
        var result = new List<Dictionary<string, string?>>();
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadFields(reader, definition.SearchFields));
        return result;
    }

    public async Task<WorkflowDocument?> LoadAsync(WorkflowDefinition definition, string caseNumber, CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        var document = new WorkflowDocument { CaseNumber = caseNumber.Trim() };
        string order = definition.IsLineBased ? $" ORDER BY {Quote("SEQ")}" : "";
        await using (var cmd = new SqlCommand($"SELECT * FROM {Quote(definition.MasterTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case{order}", cn))
        {
            cmd.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = document.CaseNumber;
            await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            if (definition.IsLineBased)
            {
                do { document.Items.Add(ReadFields(reader, definition.Fields)); }
                while (await reader.ReadAsync(ct));
                document.Fields = new Dictionary<string, string?>(document.Items[0]);
            }
            else document.Fields = ReadFields(reader, definition.Fields);
        }

        if (definition.DetailTable is not null && definition.DetailFields is not null)
        {
            await using var cmd = new SqlCommand($"SELECT TOP (1) * FROM {Quote(definition.DetailTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case", cn);
            cmd.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = document.CaseNumber;
            await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) document.Details = ReadFields(reader, definition.DetailFields);
        }
        document.Attachments = await LoadAttachmentsAsync(cn, definition, document.CaseNumber, ct);
        return document;
    }

    public async Task SaveAsync(WorkflowDefinition definition, WorkflowDocument document, CancellationToken ct)
    {
        document.CaseNumber = document.CaseNumber.Trim();
        if (string.IsNullOrWhiteSpace(document.CaseNumber)) throw new ArgumentException("登錄案號為必填。");
        if (document.CaseNumber.Length > 20) throw new ArgumentException("登錄案號不可超過 20 字元。");
        ValidateValues(definition.Fields, definition.IsLineBased ? document.Items : new[] { document.Fields });
        if (definition.DetailFields is not null) ValidateValues(definition.DetailFields, new[] { document.Details });

        await using SqlConnection cn = await OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            await DeleteRowsAsync(cn, tx, definition.MasterTable, document.CaseNumber, ct);
            if (definition.IsLineBased)
            {
                if (document.Items.Count == 0) throw new ArgumentException("開發試製案至少需要一筆物料明細。");
                for (int i = 0; i < document.Items.Count; i++)
                    await InsertRowAsync(cn, tx, definition.MasterTable, definition.Fields, document.CaseNumber, document.Items[i], i + 1, ct);
            }
            else await InsertRowAsync(cn, tx, definition.MasterTable, definition.Fields, document.CaseNumber, document.Fields, null, ct);

            if (definition.DetailTable is not null && definition.DetailFields is not null)
            {
                await DeleteRowsAsync(cn, tx, definition.DetailTable, document.CaseNumber, ct);
                await InsertRowAsync(cn, tx, definition.DetailTable, definition.DetailFields, document.CaseNumber, document.Details, null, ct);
            }
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task DeleteAsync(WorkflowDefinition definition, string caseNumber, CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
        try
        {
            if (definition.AttachmentTable is not null) await DeleteRowsAsync(cn, tx, definition.AttachmentTable, caseNumber, ct);
            if (definition.DetailTable is not null) await DeleteRowsAsync(cn, tx, definition.DetailTable, caseNumber, ct);
            await DeleteRowsAsync(cn, tx, definition.MasterTable, caseNumber, ct);
            await tx.CommitAsync(ct);
        }
        catch { await tx.RollbackAsync(ct); throw; }
    }

    public async Task<AttachmentInfo> AddAttachmentAsync(WorkflowDefinition definition, string caseNumber, string fileName, CancellationToken ct)
    {
        if (definition.AttachmentTable is null) throw new InvalidOperationException("此模組不支援附件。");
        await using SqlConnection cn = await OpenAsync(ct);
        string sql = $"INSERT INTO {Quote(definition.AttachmentTable)} ({Quote(WorkflowDefinition.KeyColumn)},{Quote("序號")},{Quote("附件")}) " +
                     $"SELECT @case,ISNULL(MAX({Quote("序號")}),0)+1,@file FROM {Quote(definition.AttachmentTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case";
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber.Trim();
        cmd.Parameters.Add("@file", SqlDbType.NVarChar, 60).Value = fileName;
        await cmd.ExecuteNonQueryAsync(ct);
        await using var find = new SqlCommand($"SELECT MAX({Quote("序號")}) FROM {Quote(definition.AttachmentTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case", cn);
        find.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber.Trim();
        int sequence = Convert.ToInt32(await find.ExecuteScalarAsync(ct));
        return new AttachmentInfo(sequence, fileName);
    }

    public async Task<AttachmentInfo> RemoveAttachmentAsync(WorkflowDefinition definition, string caseNumber, int sequence, CancellationToken ct)
    {
        if (definition.AttachmentTable is null) throw new InvalidOperationException("此模組不支援附件。");
        await using SqlConnection cn = await OpenAsync(ct);
        await using var select = new SqlCommand($"SELECT TOP (1) {Quote("附件")} FROM {Quote(definition.AttachmentTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case AND {Quote("序號")}=@sequence", cn);
        select.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber.Trim();
        select.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence;
        string? fileName = Convert.ToString(await select.ExecuteScalarAsync(ct))?.Trim();
        if (string.IsNullOrWhiteSpace(fileName)) throw new KeyNotFoundException("找不到附件資料。");
        await using var delete = new SqlCommand($"DELETE FROM {Quote(definition.AttachmentTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case AND {Quote("序號")}=@sequence", cn);
        delete.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber.Trim();
        delete.Parameters.Add("@sequence", SqlDbType.Int).Value = sequence;
        await delete.ExecuteNonQueryAsync(ct);
        return new AttachmentInfo(sequence, fileName);
    }

    public async Task<IReadOnlyList<string>> GetHolidaysAsync(CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT CONVERT(char(10),[假日],23) FROM [假日檔] ORDER BY [假日] DESC", cn);
        var values = new List<string>();
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(Text(reader[0]));
        return values;
    }

    public async Task AddHolidayAsync(DateTime date, CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("IF NOT EXISTS(SELECT 1 FROM [假日檔] WHERE CAST([假日] AS date)=@date) INSERT INTO [假日檔]([假日]) VALUES(@date)", cn);
        cmd.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteHolidayAsync(DateTime date, CancellationToken ct)
    {
        await using SqlConnection cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM [假日檔] WHERE CAST([假日] AS date)=@date", cn);
        cmd.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildSearchWhere(IEnumerable<string> fields, bool hasQuery, bool unfinishedOnly, string? completionField)
    {
        var conditions = new List<string>();
        if (hasQuery)
        {
            string search = string.Join(" OR ", fields.Select(x => $"CONVERT(nvarchar(200),{Quote(x)}) LIKE @query"));
            conditions.Add($"({search})");
        }
        if (unfinishedOnly && !string.IsNullOrWhiteSpace(completionField))
            conditions.Add($"NULLIF(RTRIM(CONVERT(nvarchar(30),{Quote(completionField)})),'') IS NULL");
        return conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
    }

    private static async Task<List<AttachmentInfo>> LoadAttachmentsAsync(SqlConnection cn, WorkflowDefinition definition, string caseNumber, CancellationToken ct)
    {
        var result = new List<AttachmentInfo>();
        if (definition.AttachmentTable is null) return result;
        await using var cmd = new SqlCommand($"SELECT {Quote("序號")},{Quote("附件")} FROM {Quote(definition.AttachmentTable)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case ORDER BY {Quote("序號")}", cn);
        cmd.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber;
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new AttachmentInfo(Convert.ToInt32(reader[0]), Text(reader[1])));
        return result;
    }

    private static async Task DeleteRowsAsync(SqlConnection cn, SqlTransaction tx, string table, string caseNumber, CancellationToken ct)
    {
        await using var cmd = new SqlCommand($"DELETE FROM {Quote(table)} WHERE {Quote(WorkflowDefinition.KeyColumn)}=@case", cn, tx);
        cmd.Parameters.Add("@case", SqlDbType.NVarChar, 20).Value = caseNumber.Trim();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertRowAsync(SqlConnection cn, SqlTransaction tx, string table, IReadOnlyList<FieldDefinition> fields, string caseNumber, IReadOnlyDictionary<string, string?> values, int? sequence, CancellationToken ct)
    {
        var names = new List<string> { WorkflowDefinition.KeyColumn };
        if (sequence.HasValue) names.Add("SEQ");
        names.AddRange(fields.Select(x => x.Name));
        string sql = $"INSERT INTO {Quote(table)} ({string.Join(',', names.Select(Quote))}) VALUES ({string.Join(',', names.Select((_, i) => $"@p{i}"))})";
        await using var cmd = new SqlCommand(sql, cn, tx);
        cmd.Parameters.Add("@p0", SqlDbType.NVarChar, 20).Value = caseNumber;
        int offset = 1;
        if (sequence.HasValue) { cmd.Parameters.Add("@p1", SqlDbType.Int).Value = sequence.Value; offset++; }
        for (int i = 0; i < fields.Count; i++)
        {
            FieldDefinition field = fields[i];
            values.TryGetValue(field.Name, out string? raw);
            object value = DbValue(raw, field);
            cmd.Parameters.AddWithValue($"@p{i + offset}", value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object DbValue(string? raw, FieldDefinition field)
    {
        string value = (raw ?? "").Trim();
        if (field.InputType == "checkbox") return value.Equals("1") || value.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (field.InputType == "date")
        {
            if (string.IsNullOrWhiteSpace(value)) return field.IsDateTime ? DBNull.Value : "";
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                throw new ArgumentException($"{field.Label}必須是有效的西元日期。");
            return field.IsDateTime ? date.ToDateTime(TimeOnly.MinValue) : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static void ValidateValues(IReadOnlyList<FieldDefinition> fields, IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        foreach (var values in rows)
        foreach (FieldDefinition field in fields)
        {
            values.TryGetValue(field.Name, out string? value);
            if (field.Required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field.Label}為必填。");
            if ((value ?? "").Length > field.MaxLength) throw new ArgumentException($"{field.Label}不可超過 {field.MaxLength} 字元。");
            if (!string.IsNullOrWhiteSpace(value) && field.Options is not null && !field.Options.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"{field.Label}選項無效。");
        }
    }

    private static Dictionary<string, string?> ReadFields(IDataRecord reader, IEnumerable<string> names)
    {
        var result = new Dictionary<string, string?>();
        foreach (string name in names)
        {
            object value = reader[name];
            result[name] = value is DBNull ? "" : value is DateTime date ? date.ToString("yyyy-MM-dd") : Convert.ToString(value)?.Trim();
        }
        return result;
    }

    private static Dictionary<string, string?> ReadFields(IDataRecord reader, IEnumerable<FieldDefinition> fields)
    {
        var result = new Dictionary<string, string?>();
        foreach (FieldDefinition field in fields)
        {
            object value = reader[field.Name];
            result[field.Name] = field.InputType == "date" ? DisplayDate(value) : value is DBNull ? "" : Convert.ToString(value)?.Trim();
        }
        return result;
    }

    private static string DisplayDate(object value)
    {
        if (value is DBNull) return "";
        if (value is DateTime dateTime) return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string text = Convert.ToString(value)?.Trim() ?? "";
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly western))
            return western.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string digits = new(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 7 &&
            int.TryParse(digits[..3], out int rocYear) &&
            int.TryParse(digits.Substring(3, 2), out int month) &&
            int.TryParse(digits.Substring(5, 2), out int day))
        {
            try { return new DateOnly(rocYear + 1911, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { }
        }
        return "";
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string Text(object value) => value is DBNull ? "" : Convert.ToString(value)?.Trim() ?? "";
}
