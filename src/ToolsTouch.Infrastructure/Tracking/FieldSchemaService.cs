using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class FieldSchemaService(RecordWorkspaceService records) : IFieldSchemaService
{
    public CollectionRecord CreateCustomCollection(CustomCollectionCreateRequest request) => records.CreateCustomCollection(request);
    public FieldDefinitionRecord CreateCustomField(CustomFieldCreateRequest request) => records.CreateCustomField(request);
    public FieldChoiceRecord CreateChoice(FieldChoiceCreateRequest request) => records.CreateChoice(request);
}
