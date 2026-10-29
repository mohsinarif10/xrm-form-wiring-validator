namespace FormWiringValidator.Model
{
    public enum Severity
    {
        Error,
        Warning
    }

    /// <summary>A single problem found on a form.</summary>
    public class Finding
    {
        public Severity Severity { get; set; }
        public string Table { get; set; }
        public string Form { get; set; }
        public string Event { get; set; }
        public string Handler { get; set; }
        public string Library { get; set; }
        public string Problem { get; set; }
        public string Fix { get; set; }
    }
}
