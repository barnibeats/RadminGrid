using System.Drawing;
using System.Windows.Forms;

namespace RadminGrid
{
    /// <summary>Тёмное оформление контекстных меню.</summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }
    }
}
