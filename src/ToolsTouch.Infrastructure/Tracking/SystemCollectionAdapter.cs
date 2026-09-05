using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class SystemCollectionAdapter(LocalDatabase database) : IRecordWorkspaceCatalog
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public IReadOnlyList<CollectionRecord> EnsureSystemCatalog()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var changed = false;
        foreach (var definition in Definitions)
        {
            using (var collection = LocalDatabase.Command(connection, """
                INSERT OR IGNORE INTO Collection(Id,Name,Kind,SystemEntityKind,Description,Revision,CreatedAt,UpdatedAt)
                VALUES($id,$name,'System',$entity,$description,1,$now,$now)
                """, ("$id", definition.Id), ("$name", definition.Name), ("$entity", definition.EntityKind),
                ("$description", definition.Description), ("$now", Now)))
            { collection.Transaction = transaction; changed |= collection.ExecuteNonQuery() != 0; }

            foreach (var field in definition.Fields)
            {
                using var fieldInsert = LocalDatabase.Command(connection, """
                    INSERT OR IGNORE INTO FieldDefinition(Id,CollectionId,Key,DisplayName,Type,StorageKind,SystemBinding,Required,EditPolicy,Revision,CreatedAt,UpdatedAt)
                    VALUES($id,$collection,$key,$display,$type,'System',$binding,$required,$policy,1,$now,$now)
                    """, ("$id", field.Id), ("$collection", definition.Id), ("$key", field.Key), ("$display", field.DisplayName),
                    ("$type", field.Type), ("$binding", field.Binding), ("$required", field.Required ? 1 : 0), ("$policy", field.EditPolicy), ("$now", Now));
                fieldInsert.Transaction = transaction;
                changed |= fieldInsert.ExecuteNonQuery() != 0;
            }
        }

        foreach (var mapping in Mappings)
        {
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = mapping.SelectSql;
            using var reader = source.ExecuteReader();
            var entities = new List<string>();
            while (reader.Read()) entities.Add(reader.GetString(0));
            reader.Close();
            foreach (var entityId in entities)
            {
                using var reference = LocalDatabase.Command(connection, """
                    INSERT OR IGNORE INTO RecordRef(Id,CollectionId,EntityKind,EntityId,CreatedAt,Revision)
                    VALUES($id,$collection,$kind,$entity,$now,1)
                    """, ("$id", mapping.CollectionId + ":" + entityId), ("$collection", mapping.CollectionId),
                    ("$kind", mapping.EntityKind), ("$entity", entityId), ("$now", Now));
                reference.Transaction = transaction;
                changed |= reference.ExecuteNonQuery() != 0;
            }
        }
        if (changed) IncrementRevision(connection, transaction);
        transaction.Commit();
        return Collections();
    }

    public IReadOnlyList<CollectionRecord> Collections()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Name,Kind,SystemEntityKind,Description,ArchivedAt,Revision FROM Collection ORDER BY Kind,Name COLLATE NOCASE,Id");
        using var reader = command.ExecuteReader();
        var result = new List<CollectionRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), NullableDate(reader, 5), reader.GetInt32(6)));
        return result;
    }

    public IReadOnlyList<FieldDefinitionRecord> Fields(string collectionId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,CollectionId,Key,DisplayName,Type,StorageKind,SystemBinding,OptionsJson,Required,EditPolicy,ArchivedAt,Revision
            FROM FieldDefinition WHERE CollectionId=$collection ORDER BY ArchivedAt IS NOT NULL,Key COLLATE NOCASE,Id
            """, ("$collection", collectionId));
        using var reader = command.ExecuteReader();
        var result = new List<FieldDefinitionRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            NullableString(reader, 6), NullableString(reader, 7), reader.GetInt64(8) != 0, reader.GetString(9), NullableDate(reader, 10), reader.GetInt32(11)));
        return result;
    }

    public IReadOnlyList<RecordRefRecord> Records(string collectionId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,CollectionId,EntityKind,EntityId,CreatedAt,ArchivedAt,Revision FROM RecordRef WHERE CollectionId=$collection ORDER BY Id", ("$collection", collectionId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordRefRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), Date(reader, 4), NullableDate(reader, 5), reader.GetInt32(6)));
        return result;
    }

    public IReadOnlyList<FieldChoiceRecord> Choices(string fieldId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,FieldId,Label,ColorToken,SortOrder,ArchivedAt FROM FieldChoice WHERE FieldId=$field ORDER BY SortOrder,Id", ("$field", fieldId));
        using var reader = command.ExecuteReader();
        var result = new List<FieldChoiceRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), reader.GetInt32(4), NullableDate(reader, 5)));
        return result;
    }

    public static IReadOnlyList<SystemCollectionDefinition> Definitions { get; } =
    [
        Definition("system-schools", "学校", "School", "学校领域记录", [
            Field("school-name", "name", "名称", "Text", "School.CanonicalName", "SystemManaged", true),
            Field("school-short-name", "short_name", "简称", "Text", "School.ShortName", "SystemManaged", false),
            Field("school-city", "city", "城市", "Text", "School.City", "SystemManaged", false)]),
        Definition("system-departments", "学院／系", "Department", "学院、系和研究院领域记录", [
            Field("department-name", "name", "名称", "Text", "Department.CanonicalName", "SystemManaged", true),
            Field("department-school", "school", "学校", "Relation", "Department.SchoolId", "SystemManaged", true),
            Field("department-kind", "kind", "类型", "Text", "Department.Kind", "SystemManaged", true)]),
        Definition("system-programs", "招生项目", "AdmissionProgram", "招生项目领域记录", [
            Field("program-name", "name", "名称", "Text", "AdmissionProgram.Name", "SystemManaged", true),
            Field("program-degree", "degree_type", "学位类型", "Text", "AdmissionProgram.DegreeType", "SystemManaged", true),
            Field("program-department", "department", "学院", "Relation", "AdmissionProgram.DepartmentId", "SystemManaged", true)]),
        Definition("system-rounds", "招生轮次", "AdmissionRound", "招生批次和窗口领域记录", [
            Field("round-title", "title", "标题", "Text", "AdmissionRound.Title", "SystemManaged", true),
            Field("round-cycle-year", "cycle_year", "活动年份", "Number", "AdmissionRound.CycleYear", "SystemManaged", false),
            Field("round-application-url", "application_url", "报名链接", "Url", "AdmissionRound.ApplicationUrl", "SystemManaged", false)]),
        Definition("system-professors", "导师", "Professor", "导师领域记录", [
            Field("professor-name", "name", "姓名", "Text", "Professor.Name", "SystemManaged", true),
            Field("professor-institution", "institution", "机构原文", "Text", "Professor.Institution", "SystemManaged", true),
            Field("professor-email", "email", "公开邮箱", "Text", "Professor.Email", "SystemManaged", false)]),
        Definition("system-applications", "官网申请", "ApplicationCase", "官网申请和申请阶段领域记录", [
            Field("application-stage", "stage", "申请阶段", "Choice", "ApplicationCase.Stage", "OverrideWithReason", true),
            Field("application-priority", "priority", "优先级", "Number", "ApplicationCase.Priority", "Editable", false),
            Field("application-url", "application_url", "报名链接", "Url", "ApplicationCase.ApplicationUrl", "Editable", false),
            Field("application-next-step", "next_step", "下一步", "Text", "ApplicationCase.NextStep", "Editable", false),
            Field("application-note", "owner_note", "备注", "Text", "ApplicationCase.OwnerNote", "Editable", false),
            Field("application-submitted-at", "submitted_at", "官网提交时间", "DateTime", "ApplicationCase.SubmittedAt", "OverrideWithReason", false)]),
        Definition("system-outreach", "邮件联系", "Outreach", "邮件草稿和发送事实领域记录", [
            Field("outreach-subject", "subject", "主题", "Text", "Outreach.Subject", "SystemManaged", true),
            Field("outreach-state", "state", "发送状态", "Choice", "Outreach.State", "SystemManaged", true),
            Field("outreach-recipient", "recipient", "收件人", "Text", "Outreach.Recipient", "SystemManaged", true)]),
        Definition("system-tasks", "任务", "AgentRun", "任务和阶段状态领域记录", [
            Field("task-kind", "kind", "类型", "Text", "AgentRun.Kind", "SystemManaged", true),
            Field("task-state", "state", "状态", "Choice", "AgentRun.State", "SystemManaged", true)])
    ];

    private static SystemCollectionDefinition Definition(string id, string name, string entityKind, string description, IReadOnlyList<SystemFieldDefinition> fields) => new(id, name, entityKind, description, fields);
    private static SystemFieldDefinition Field(string id, string key, string displayName, string type, string binding, string editPolicy, bool required) => new(id, key, displayName, type, binding, editPolicy, required);

    private static IReadOnlyList<SystemMapping> Mappings { get; } =
    [
        new("system-schools", "School", "SELECT Id FROM School WHERE ArchivedAt IS NULL"),
        new("system-departments", "Department", "SELECT Id FROM Department WHERE ArchivedAt IS NULL"),
        new("system-programs", "AdmissionProgram", "SELECT Id FROM AdmissionProgram WHERE ArchivedAt IS NULL"),
        new("system-rounds", "AdmissionRound", "SELECT Id FROM AdmissionRound WHERE ArchivedAt IS NULL"),
        new("system-professors", "Professor", "SELECT Id FROM Professor"),
        new("system-applications", "ApplicationCase", "SELECT Id FROM ApplicationCase"),
        new("system-outreach", "Outreach", "SELECT Id FROM Outreach"),
        new("system-tasks", "AgentRun", "SELECT Id FROM AgentRun")
    ];

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }

    private static DateTimeOffset Date(SqliteDataReader reader, int index) => DateTimeOffset.Parse(reader.GetString(index));
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Date(reader, index);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private sealed record SystemMapping(string CollectionId, string EntityKind, string SelectSql);
}
