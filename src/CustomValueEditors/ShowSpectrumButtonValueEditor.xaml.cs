﻿//-----------------------------------------------------------------------------
// Copyright (c) 2003-2015, Thermo Fisher Scientific
// All rights reserved
//-----------------------------------------------------------------------------

using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Infragistics.Windows.DataPresenter;
using Infragistics.Windows.Editors;
using Thermo.Discoverer.EntityDataFramework.Controls.HierarchicalEntityItemDataGrid.Models;
using Thermo.Discoverer.SampleNodes.CustomValueEditors;
using Thermo.Magellan.BL.Data;
using Thermo.Magellan.BL.ReportProcessing;
using Thermo.Magellan.EntityDataFramework;
using Thermo.Magellan.EntityDataFramework.ReportFile;
using Thermo.PD.EntityDataFramework;

namespace Thermo.Discoverer.EntityDataFramework.Controls.GenericGridControl.CustomValueEditors
{
	/// <summary>
    /// Interaction logic ShowSpectrumButtonValueEditor.xaml. This class essentially handles the OnButtonPressed event, i.e, 
    /// it diaplays a spectrum using an ad-hoc spectrum view.
	/// </summary>
    [ApplicationExtension("WPFGridControlExtension", "7875B499-672B-40D7-838E-91B65C7471E2", typeof(ICustomValueEditor))]
    public partial class ShowSpectrumButtonValueEditor : ICustomValueEditor
	{
	    private IEntityDataService m_entityDataService;

	    /// <summary>
		/// Initializes a new instance of the <see cref="CheckImageValueEditor" /> class.
		/// </summary>
	    public ShowSpectrumButtonValueEditor()
		{
			InitializeComponent();
			IsToolTipEnabled = false;
		}


        /// <summary>
        /// This property is always true and is used as a kludge in the data trigger for storing the cell 
        /// value in the button's tag. 
        /// </summary>
        /// <value>
        ///   <c>true</c> if always; otherwise, <c>false</c>.
        /// </value>
        public bool Always { get { return true; } }

        /// <summary>
        /// Gets the button text. Set this to something appropriate if needed. 
        /// Note: It woud be possible to generate a dynamic text here (e.g., using the value property).
        /// </summary>
        /// <value>
        /// The button text.
        /// </value>
        public string ButtonText { get { return "Show Spectrum"; } }


        /// <summary>
        /// Prepares the <see cref="ValueEditor" /> to be used to display the data of a specific entity data property with a custom cell value control.
        /// </summary>
        /// <param name="dataField">The field used in the FieldLayout's <see cref="FieldLayout.Fields" /> collection to define the layout of a single field.</param>
        /// <param name="entityDataService">(optional) The <see cref="IEntityDataService" /> of the underlying <see cref="IEntityViewModel" /> which provides the grid data.</param>
        /// <param name="propertyColumn">(optional) The property column which represents a property of the entity data type which is display in this grid column</param>
        /// <remarks>
        /// Only the <b>Type</b> of the value editor to use is specified here, the <see cref="XamDataPresenter" /> creates then the value editor using this type information. Therefore it
        /// is not possible to provide custom data / information by using member variables, because the editor instance will be created on demand from the data presenter.
        /// The only way to provide additional data for the value editor of a column is to add this information somehow to the <see cref="FieldSettings.EditorStyle" /> and to set them to
        /// the value editor instance in an override of <see cref="ValueEditor.ApplyTemplate" /> method.
        /// </remarks>
	    public void PrepareEditorDataField(Field dataField, IEntityDataService entityDataService = null, PropertyColumn propertyColumn = null)
        {
            PrepareEditorStyle<ShowSpectrumButtonValueEditor>(dataField);
	        m_entityDataService = entityDataService;
        }

		/// <summary>
		/// Determines whether this instance can edit the specified type.
		/// </summary>
		/// <param name="type">The type of the object to edit.</param>
		/// <returns>
		/// Deliberately always false.
		/// </returns>
		public override bool CanEditType(Type type)
		{
			return false;
		}

        /// <summary>
        /// Determines whether this instance can render the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns></returns>
	    public override bool CanRenderType(Type type)
	    {
	        return true;
	    }

        /// <summary>
        /// Called when button is pressed.
        /// Reads the corresponding MsnSpectrumInfo and MassSpectrum objects for a PSM and displays some examplary contents 
        /// using a simple ad-hoc spectrum view. 
        /// </summary>
        /// <param name="cellContents">The cell contents.</param>
        public void OnButtonPressed(object cellContents)
        {
            // Note: The cell content is a string that contains the ids of the PSM (workflow ID and peptide ID)
            // and in addition the string describing all fragment annotations concatenated using ';'.
            // This is a simple hack to enable reading the entire spectrum by its IDs and also handing over the
            // fragment annotation information without having to read the entire RNPxl row again.
            // The cell contents is set by the node (see RNPxlConsensusNode.cs) especially for this. 
            
            if (m_entityDataService == null || !(cellContents is string))
            {
                ShowCouldNotShowSpectrumError("Unexpected data");
                return;
            }

            var strings = ((string)cellContents).Split(new [] {';'}, StringSplitOptions.None);

            if (strings.Count() != 3)
            {
                ShowCouldNotShowSpectrumError("Unexpected number of IDs");
                return;
            }

            string annotations = strings[2];
            string[] idStrings = {strings[0], strings[1]};
            
            object[] ids;
            try
            {
                ids = idStrings.Select(id => Convert.ToInt32(id) as object).ToArray();
            }
            catch (Exception)
            {
                ShowCouldNotShowSpectrumError("Unable to decode id data");
                return;
            }

            try
            {
                // Now read the corresponding spectrum using the EntityDataService.
                var reader = m_entityDataService.CreateEntityItemReader();
                var spectrumInfo = reader.Read<MSnSpectrumInfo>(ids);

                // Actually, use the DiscoveryEntityDataService to read the whole spectrum. 
                // In PD this cast should always succeed, but we check anyway.
                var dds = m_entityDataService as DiscovererEntityDataService;
                if (dds == null)
                {
                    ShowCouldNotShowSpectrumError("Discoverer Entity Data Service not available");
                    return;
                }

                var spectrum = dds.GetSpectrum(spectrumInfo);

                string ot = "m/z " + String.Format("{0:0.0000}", spectrumInfo.MassOverCharge) + "  |  RT " + String.Format("{0:0.00}", spectrumInfo.RetentionTime) + "  |  Charge " + spectrumInfo.Charge;

                var view = new SpectrumView
                           {
                               Title = ot,
                               Annotations = annotations,
                               // Show centroids when available, otherwise profiles.
                               PeakList = spectrum.HasPeakCentroids ? spectrum.PeakCentroids.Select(c => Tuple.Create(c.Position, c.Intensity)).ToList() : spectrum.ProfilePoints.ToList().Select(p => Tuple.Create(p.Position, p.Intensity)).ToList()
                           };
                view.ShowDialog();
            }
            catch (Exception)
            {
                ShowCouldNotShowSpectrumError("Please make sure the 'Spectra to store' parameter in the 'MSF Files' node of your consensus workflow is set to 'All'. If it is not, please set it to 'All' and rerun the consensus workflow. If it is, you've found a bug. Please report it to the OpenMS developers");
                return;
            }
        }

        /// <summary>
        /// Shows an error message using a simple message box.
        /// </summary>
        /// <param name="additionalMessage">The MSG.</param>
	    private void ShowCouldNotShowSpectrumError(string additionalMessage = "")
	    {
	        MessageBox.Show(additionalMessage == "" ? "Could not show spectrum." : String.Format("Could not show spectrum: {0}.", additionalMessage), "Error");
	    }

        /// <summary>
        /// Handles the OnClick event of the PART_LinkButton control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="RoutedEventArgs"/> instance containing the event data.</param>
	    private void PART_LinkButton_OnClick(object sender, RoutedEventArgs e)
	    {
	        var button = sender as Button;
	        if (button != null)
	        {
                // Pass the Tag to the handler routine.
                OnButtonPressed(button.Tag);
	        }
	    }
	}
}
