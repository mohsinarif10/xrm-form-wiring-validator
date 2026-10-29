using System.ComponentModel.Composition;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace FormWiringValidator
{
    [Export(typeof(IXrmToolBoxPlugin))]
    [ExportMetadata("Name", "Form Wiring Validator")]
    [ExportMetadata("Description",
        "Finds broken form script wiring: handlers whose function is missing from the declared " +
        "library, libraries that do not exist, and controls bound to columns that are gone.")]
    [ExportMetadata("BackgroundColor", "WhiteSmoke")]
    [ExportMetadata("PrimaryFontColor", "Black")]
    [ExportMetadata("SecondaryFontColor", "DimGray")]
    public class FormWiringValidatorPlugin : PluginBase
    {
        public override IXrmToolBoxPluginControl GetControl()
        {
            return new FormWiringValidatorControl();
        }
    }
}
