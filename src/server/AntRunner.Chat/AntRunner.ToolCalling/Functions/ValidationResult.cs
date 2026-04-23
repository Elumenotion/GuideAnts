namespace AntRunner.ToolCalling.Functions
{
    /// <summary>
    /// Represents the result of OpenAPI spec validation.
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// Gets or sets the status of the validation.
        /// </summary>
        public bool Status { get; set; }

        /// <summary>
        /// Gets or sets the validation error or success message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the parsed OpenAPI specification.
        /// </summary>
        public JsonDocument? Spec { get; set; }
    }
}
