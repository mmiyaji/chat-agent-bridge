# Covers the primary monitor with a plain, non-activating background so only the demo windows appear on camera.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Drawing.Primitives, System.ComponentModel.Primitives, System.Windows.Forms.Primitives -TypeDefinition @'
using System.Windows.Forms;
public class Backdrop : Form {
  public Backdrop() {
    FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
    BackColor = System.Drawing.Color.FromArgb(32, 39, 51); Text = "CAB demo backdrop";
    // Work area only: a monitor-sized window makes Windows treat it as full screen and hold back notifications.
    Bounds = Screen.PrimaryScreen.WorkingArea;
  }
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }
}
'@
[System.Windows.Forms.Application]::Run([Backdrop]::new())
