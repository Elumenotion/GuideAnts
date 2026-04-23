namespace GuideAntsApi.DataModel.Models
{
    /// <summary>
    /// Discriminator for assistant records.
    /// </summary>
    public enum AssistantKind : byte
    {
        Assistant = 0,
        Guide = 1
    }
}


