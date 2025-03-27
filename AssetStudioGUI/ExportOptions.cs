using AssetStudio;
using System;
using System.Linq;
using System.Windows.Forms;

namespace AssetStudioGUI
{
    public partial class ExportOptions : Form
    {
        private static Fbx.Settings fbxSettings;

        public ExportOptions()
        {
            InitializeComponent();
            assetGroupOptions.SelectedIndex = Properties.Settings.Default.assetGroupOption;
            filenameFormatComboBox.SelectedIndex = Properties.Settings.Default.filenameFormat;
            restoreExtensionName.Checked = Properties.Settings.Default.restoreExtensionName;
            converttexture.Checked = Properties.Settings.Default.convertTexture;
            exportSpriteWithAlphaMask.Checked = Properties.Settings.Default.exportSpriteWithMask;
            convertAudio.Checked = Properties.Settings.Default.convertAudio;
            var defaultImageType = Properties.Settings.Default.convertType.ToString();
            ((RadioButton)panel1.Controls.Cast<Control>().First(x => x.Text == defaultImageType)).Checked = true;
            openAfterExport.Checked = Properties.Settings.Default.openAfterExport;
            var maxParallelTasks = Environment.ProcessorCount;
            var taskCount = Properties.Settings.Default.parallelExportCount;
            parallelExportUpDown.Maximum = maxParallelTasks;
            parallelExportUpDown.Value = taskCount <= 0 ? maxParallelTasks : Math.Min(taskCount, maxParallelTasks);
            parallelExportMaxLabel.Text += maxParallelTasks;
            parallelExportCheckBox.Checked = Properties.Settings.Default.parallelExport;
           
            l2dModelGroupComboBox.SelectedIndex = (int)Properties.Settings.Default.l2dModelGroupOption;
            l2dAssetSearchByFilenameCheckBox.Checked = Properties.Settings.Default.l2dAssetSearchByFilename;
            var defaultMotionMode = Properties.Settings.Default.l2dMotionMode.ToString();
            ((RadioButton)l2dMotionExportMethodPanel.Controls.Cast<Control>().First(x => x.AccessibleName == defaultMotionMode)).Checked = true;
            l2dForceBezierCheckBox.Checked = Properties.Settings.Default.l2dForceBezier;

            fbxSettings = Fbx.Settings.FromBase64(Properties.Settings.Default.fbxSettings);
            SetFromFbxSettings();
        }

        private void OKbutton_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.assetGroupOption = assetGroupOptions.SelectedIndex;
            Properties.Settings.Default.filenameFormat = filenameFormatComboBox.SelectedIndex;
            Properties.Settings.Default.restoreExtensionName = restoreExtensionName.Checked;
            Properties.Settings.Default.convertTexture = converttexture.Checked;
            Properties.Settings.Default.exportSpriteWithMask = exportSpriteWithAlphaMask.Checked;
            Properties.Settings.Default.convertAudio = convertAudio.Checked;
            var checkedImageType = (RadioButton)panel1.Controls.Cast<Control>().First(x => ((RadioButton)x).Checked);
            Properties.Settings.Default.convertType = (ImageFormat)Enum.Parse(typeof(ImageFormat), checkedImageType.Text);
            Properties.Settings.Default.openAfterExport = openAfterExport.Checked;
            Properties.Settings.Default.parallelExport = parallelExportCheckBox.Checked;
            Properties.Settings.Default.parallelExportCount = (int)parallelExportUpDown.Value;

            Properties.Settings.Default.l2dModelGroupOption = (CubismLive2DExtractor.Live2DModelGroupOption)l2dModelGroupComboBox.SelectedIndex;
            Properties.Settings.Default.l2dAssetSearchByFilename = l2dAssetSearchByFilenameCheckBox.Checked;
            var checkedMotionMode = (RadioButton)l2dMotionExportMethodPanel.Controls.Cast<Control>().First(x => ((RadioButton)x).Checked);
            Properties.Settings.Default.l2dMotionMode = (CubismLive2DExtractor.Live2DMotionMode)Enum.Parse(typeof(CubismLive2DExtractor.Live2DMotionMode), checkedMotionMode.AccessibleName);
            Properties.Settings.Default.l2dForceBezier = l2dForceBezierCheckBox.Checked;

            fbxSettings.EulerFilter = eulerFilter.Checked;
            fbxSettings.FilterPrecision = (float)filterPrecision.Value;
            fbxSettings.ExportAllNodes = exportAllNodes.Checked;
            fbxSettings.ExportSkins = exportSkins.Checked;
            fbxSettings.ExportAnimations = exportAnimations.Checked;
            fbxSettings.ExportBlendShape = exportBlendShape.Checked;
            fbxSettings.CastToBone = castToBone.Checked;
            fbxSettings.ExportAllUvsAsDiffuseMaps = exportAllUvsAsDiffuseMaps.Checked;
            fbxSettings.BoneSize = (int)boneSize.Value;
            fbxSettings.ScaleFactor = (float)scaleFactor.Value;
            fbxSettings.FbxVersionIndex = fbxVersion.SelectedIndex;
            fbxSettings.FbxFormat = fbxFormat.SelectedIndex;
            for (var i = 0; i < uvIndicesCheckedListBox.Items.Count; i++)
            {
                var isChecked = uvIndicesCheckedListBox.GetItemChecked(i);
                var type = fbxSettings.UvBindings[i];
                if ((isChecked && type < 0) || (!isChecked && type > 0))
                    fbxSettings.UvBindings[i] *= -1;
            }
            Properties.Settings.Default.fbxSettings = fbxSettings.ToBase64();

            Properties.Settings.Default.Save();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void Cancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void parallelExportCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            parallelExportUpDown.Enabled = parallelExportCheckBox.Checked;
        }

        private void uvIndicesCheckedListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (exportAllUvsAsDiffuseMaps.Checked)
                return;

            if (fbxSettings.UvBindings.TryGetValue(uvIndicesCheckedListBox.SelectedIndex, out var uvType))
            {
                uvTypesListBox.SelectedIndex = (int)MathF.Abs(uvType) - 1;
            }
        }

        private void uvTypesListBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            var selectedUv = uvIndicesCheckedListBox.SelectedIndex;
            fbxSettings.UvBindings[selectedUv] = uvTypesListBox.SelectedIndex + 1;
        }

        private void exportAllUvsAsDiffuseMaps_CheckedChanged(object sender, EventArgs e)
        {
            uvTypesListBox.Enabled = !exportAllUvsAsDiffuseMaps.Checked;
            uvIndicesCheckedListBox.Enabled = !exportAllUvsAsDiffuseMaps.Checked;
        }

        private void SetFromFbxSettings()
        {
            eulerFilter.Checked = fbxSettings.EulerFilter;
            filterPrecision.Value = (decimal)fbxSettings.FilterPrecision;
            exportAllNodes.Checked = fbxSettings.ExportAllNodes;
            exportSkins.Checked = fbxSettings.ExportSkins;
            exportAnimations.Checked = fbxSettings.ExportAnimations;
            exportBlendShape.Checked = fbxSettings.ExportBlendShape;
            castToBone.Checked = fbxSettings.CastToBone;
            exportAllUvsAsDiffuseMaps.Checked = fbxSettings.ExportAllUvsAsDiffuseMaps;
            boneSize.Value = (decimal)fbxSettings.BoneSize;
            scaleFactor.Value = (decimal)fbxSettings.ScaleFactor;
            fbxVersion.SelectedIndex = fbxSettings.FbxVersionIndex;
            fbxFormat.SelectedIndex = fbxSettings.FbxFormat;
            for (var i = 0; i < uvIndicesCheckedListBox.Items.Count; i++)
            {
                var isChecked = fbxSettings.UvBindings[i] > 0;
                uvIndicesCheckedListBox.SetItemChecked(i, isChecked);
            }
            uvTypesListBox.Enabled = !exportAllUvsAsDiffuseMaps.Checked;
            uvIndicesCheckedListBox.Enabled = !exportAllUvsAsDiffuseMaps.Checked;
        }

        private void resetButton_Click(object sender, EventArgs e)
        {
            fbxSettings.Init();
            SetFromFbxSettings();
            uvIndicesCheckedListBox_SelectedIndexChanged(sender, e);
        }
    }
}
