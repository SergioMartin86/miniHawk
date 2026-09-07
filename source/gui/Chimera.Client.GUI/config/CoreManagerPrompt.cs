#nullable enable

using System.Drawing;
using System.Windows.Forms;

namespace Chimera.Client.GUI
{
	/// <summary>
	/// What a fresh install is told, once: Chimera ships no cores, so until one is
	/// installed nothing it can open exists.
	///
	/// A sentence and a choice, rather than the Core Manager opening by itself.
	/// Somebody who has just started the program for the first time should meet
	/// the program, not a window they did not ask for over an application they
	/// have not seen yet.
	/// </summary>
	public sealed class CoreManagerPrompt : FormBase
	{
		protected override string WindowTitleStatic => "No cores installed";

		public CoreManagerPrompt()
		{
			SuspendLayout();
			ClientSize = new(UIHelper.ScaleX(430), UIHelper.ScaleY(110));
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition = FormStartPosition.CenterParent;
			MaximizeBox = false;
			MinimizeBox = false;
			ShowIcon = false;
			ShowInTaskbar = false;

			var margin = UIHelper.ScaleX(12);
			Label message = new()
			{
				AutoSize = false,
				Location = new(margin, UIHelper.ScaleY(14)),
				Size = new(ClientSize.Width - (2 * margin), UIHelper.ScaleY(44)),
				Text = "You have currently no emulation cores installed. Please open the Core Manager to install them.",
			};

			var buttonWidth = UIHelper.ScaleX(150);
			var buttonRow = ClientSize.Height - UIHelper.ScaleY(38);

			Button open = new()
			{
				DialogResult = DialogResult.OK,
				Location = new(ClientSize.Width - margin - buttonWidth, buttonRow),
				Size = new(buttonWidth, UIHelper.ScaleY(26)),
				Text = "Open Core Manager",
			};

			Button cancel = new()
			{
				DialogResult = DialogResult.Cancel,
				Location = new(ClientSize.Width - margin - (2 * buttonWidth) - UIHelper.ScaleX(8), buttonRow),
				Size = new(buttonWidth, UIHelper.ScaleY(26)),
				Text = "Cancel",
			};

			Controls.AddRange(new Control[] { message, cancel, open });
			AcceptButton = open;
			CancelButton = cancel;
			ResumeLayout();
		}
	}
}
