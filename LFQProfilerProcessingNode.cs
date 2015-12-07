﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using Thermo.Magellan.BL.Data;
using Thermo.Magellan.BL.Processing;
using Thermo.Magellan.BL.Processing.Interfaces;
using Thermo.Magellan.DataLayer.FileIO;
using Thermo.Magellan.EntityDataFramework;
using Thermo.Magellan.Exceptions;
using Thermo.Magellan.MassSpec;
using Thermo.Magellan.Utilities;

using System.Web.UI;

using System.Xml.Linq;
using System.Text;

namespace PD.OpenMS.AdapterNodes
{
    #region Node Setup

    [ProcessingNode("60F7FFAC-F6BF-446E-8504-498D0919B130",
        Category = ProcessingNodeCategories.Quantification,
        DisplayName = "LFQProfiler FF",
        MainVersion = 1,
        MinorVersion = 50,
        Description = "Detects and quantifies peptide features in the data using the OpenMS framework.")]

    [ConnectionPoint("IncomingSpectra",
        ConnectionDirection = ConnectionDirection.Incoming,
        ConnectionMultiplicity = ConnectionMultiplicity.Single,
        ConnectionMode = ConnectionMode.Manual,
        ConnectionRequirement = ConnectionRequirement.RequiredAtDesignTime,
        ConnectionDisplayName = ProcessingNodeCategories.SpectrumAndFeatureRetrieval,
        ConnectionDataHandlingType = ConnectionDataHandlingType.InMemory)]

    [ConnectionPointDataContract(
        "IncomingSpectra",
        MassSpecDataTypes.MSnSpectra)]

    [ConnectionPoint("featureXML",
        ConnectionDirection = ConnectionDirection.Outgoing,
        ConnectionMultiplicity = ConnectionMultiplicity.Multiple,
        ConnectionMode = ConnectionMode.Manual,
        ConnectionRequirement = ConnectionRequirement.Optional,
        ConnectionDisplayName = ProcessingNodeCategories.DataInput,
        ConnectionDataHandlingType = ConnectionDataHandlingType.FileBased)]

    [ConnectionPointDataContract(
        "featureXML",
        "featureXML")]

    [ProcessingNodeConstraints(UsageConstraint = UsageConstraint.OnlyOncePerWorkflow)]

    #endregion

    public class LFQProfilerProcessingNode : ProcessingNode,
        IResultsSink<MassSpectrumCollection>
    {
        #region Parameters

        [MassToleranceParameter(
            Category = "1. Feature Finding",
            DisplayName = "Mass tolerance",
            Description = "This parameter specifies the mass tolerance for feature detection",
            DefaultValue = "10 ppm",
            Position = 1,
            IntendedPurpose = ParameterPurpose.MassTolerance)]
        public MassToleranceParameter param_mass_tolerance;

        [IntegerParameter(Category = "1. Feature Finding",
            DisplayName = "Charge Low",
            Description = "Lowest charge state to search for.",
            DefaultValue = "1",
            MinimumValue = "1",
            Position = 2)]
        public IntegerParameter param_charge_low;

        [IntegerParameter(Category = "1. Feature Finding",
            DisplayName = "Charge High",
            Description = "Highest charge state to search for.",
            DefaultValue = "5",
            MinimumValue = "1",
            Position = 3)]
        public IntegerParameter param_charge_high;

        [DoubleParameter(Category = "1. Feature Finding",
            DisplayName = "Typical RT",
            Description = "Typical retention time of a feature [sec]",
            DefaultValue = "30",
            MinimumValue = "0",
            Position = 4)]
        public DoubleParameter param_typical_rt;

        [DoubleParameter(Category = "1. Feature Finding",
            DisplayName = "Minimum RT",
            Description = "Minimum retention time of a feature [sec]",
            DefaultValue = "3",
            MinimumValue = "0",
            Position = 5)]
        public DoubleParameter param_minimum_rt;

        [DoubleParameter(Category = "1. Feature Finding",
            DisplayName = "Averagine similarity",
            Description = "Lower bound on similarity of observed isotopic pattern and averagine model.",
            DefaultValue = "0.3",
            MinimumValue = "0",
            MaximumValue = "1",
            Position = 6)]
        public DoubleParameter param_averagine_similarity;

        #endregion

        private int m_current_step;
        private int m_num_steps;
        private int m_num_files;
        private readonly SpectrumDescriptorCollection m_spectrum_descriptors = new SpectrumDescriptorCollection();
        private List<WorkflowInputFile> m_workflow_input_files;

        #region Top-level program flow

        /// <summary>
        /// Initializes the progress.
        /// </summary>
        /// <returns></returns>
        public override ProgressInitializationHint InitializeProgress()
        {
            return new ProgressInitializationHint(4 * ProcessingServices.CurrentWorkflow.GetWorkflow().GetWorkflowInputFiles().ToList().Count, ProgressDependenceType.Independent);
        }

        /// <summary>
        /// Portion of mass spectra received.
        /// </summary>
        public void OnResultsSent(IProcessingNode sender, MassSpectrumCollection result)
        {
            ArgumentHelper.AssertNotNull(result, "result");
            m_spectrum_descriptors.AddRange(ProcessingServices.SpectrumProcessingService.StoreSpectraInCache(this, result));
        }

        /// <summary>
        /// Called when the parent node finished the data processing.
        /// </summary>
        /// <param name="sender">The parent node.</param>
        /// <param name="eventArgs">The result event arguments.</param>
        public override void OnParentNodeFinished(IProcessingNode sender, ResultsArguments eventArgs)
        {
            // determine number of input files which have to be converted
            m_workflow_input_files = EntityDataService.CreateEntityItemReader().ReadAll<WorkflowInputFile>().ToList();
            m_num_files = m_workflow_input_files.Count;

            // set up approximate progress bars
            m_current_step = 0;
            m_num_steps = 1 + 2 * m_num_files;

            var raw_files = new List<string>(m_num_files);
            var exported_files = new List<string>(m_num_files);

            // Group spectra by file id and process 
            foreach (var spectrumDescriptorsGroupedByFileId in m_spectrum_descriptors
                .Where(w => (w.ScanEvent.MSOrder == MSOrderType.MS1))//.Where(w=>w.ScanEvent.MSOrder == MSOrderType.MS1) //if we remove, we get 1 spec per file
                .GroupBy(g => g.Header.FileID))
            {
                // Group by the scan event of the MS1 spectrum to avoid mixing up different polarities or scan ranges
                foreach (var grp in spectrumDescriptorsGroupedByFileId.GroupBy(g => g.ScanEvent))
                {
                    int file_id = spectrumDescriptorsGroupedByFileId.Key;

                    // Flatten the spectrum tree to a collection of spectrum descriptors. 
                    var spectrum_descriptors = grp.ToList();

                    // Export spectra to temporary *.mzML file. Only one file has this file_id
                    var file_to_export = m_workflow_input_files.Where(w => w.FileID == file_id).ToList().First().PhysicalFileName;
                    var spectrum_export_file_name = Path.Combine(NodeScratchDirectory, Path.GetFileNameWithoutExtension(file_to_export)) + "_" + Guid.NewGuid().ToString().Replace('-', '_') + ".mzML";

                    raw_files.Add(file_to_export);
                    exported_files.Add(spectrum_export_file_name);

                    ExportSpectraToMzMl(spectrum_descriptors, spectrum_export_file_name);

                    m_current_step += 1;
                    ReportTotalProgress((double)m_current_step / m_num_steps);
                }
            }

            // Remove obsolete CV terms written by Thermo's mzML export code
            FixCVTerms(exported_files);

            // Store file names
            var custom_raw_data_field = ProcessingServices.CustomDataService.GetOrCreateCustomDataField(WorkflowID, new Guid("1301FADF-988F-48D6-AC68-AD5BD2A7841A"), "RawFileNames", ProcessingNodeNumber, ProcessingNodeNumber, CustomDataTarget.ProcessingNode, CustomDataType.String, CustomDataAccessMode.Read, DataVisibility.Hidden, dataPurpose: "RawFiles");
            var raw_files_string = string.Join(",", raw_files.ToArray());
            ProcessingServices.CustomDataService.WriteString(custom_raw_data_field, ProcessingNodeNumber, raw_files_string);
            var custom_mzml_data_field = ProcessingServices.CustomDataService.GetOrCreateCustomDataField(WorkflowID, new Guid("9E011E9D-1AB3-410C-9BD7-A1AD95B67F26"), "MzMLFileNames", ProcessingNodeNumber, ProcessingNodeNumber, CustomDataTarget.ProcessingNode, CustomDataType.String, CustomDataAccessMode.Read, DataVisibility.Hidden, dataPurpose: "MzMLFiles");
            var mzml_files_string = string.Join(",", exported_files.ToArray());
            ProcessingServices.CustomDataService.WriteString(custom_mzml_data_field, ProcessingNodeNumber, mzml_files_string);

            // Run pipeline
            RunOpenMsPipeline(exported_files);

            // Fire Finish event
            FireProcessingFinishedEvent(new ResultsArguments());

            ReportTotalProgress(1.0);
        }

        /// <summary>
        /// Remove obsolete CV terms from spectrum export which otherwise lead to delay and warnings when loading into OpenMS
        /// </summary>
        private void FixCVTerms(List<string> mzml_files)
        {
            foreach (var f in mzml_files)
            {
                // move to temporary file
                var tmp_f = f.Replace(".mzML", "_tmp.mzML");
                try
                {
                    File.Move(f, tmp_f);
                }
                catch (Exception)
                {
                    SendAndLogErrorMessage("Could not move file {0} to {1}", f, tmp_f);
                }

                // open temporary file, remove obsolete CV terms, store with original filename
                XDocument doc = XDocument.Load(tmp_f);
                var q = from node in doc.Descendants("{http://psi.hupo.org/ms/mzml}cvParam")
                        let acc = node.Attribute("accession")
                        where acc != null && acc.Value == "MS:1000498"
                        select node;
                q.ToList().ForEach(x => x.Remove());
                try
                {
                    doc.Save(f);
                }
                catch (Exception)
                {
                    SendAndLogErrorMessage("Could not save file {0}", f);
                }

                // remove temporary file
                try
                {
                    File.Delete(tmp_f);
                }
                catch (Exception)
                {
                    SendAndLogErrorMessage("Could not delete file {0}", tmp_f);
                }
            }
        }

        #endregion

        #region mzML export

        /// <summary>
        /// Exports the correspoding spectra to a new created mzML.
        /// </summary>
        /// <param name="spectrumDescriptorsGroupByFileId">The spectrum descriptors grouped by file identifier.</param>
        /// <returns>The file name of the new created mzML file, containing the exported spectra.</returns>
        /// <exception cref="Thermo.Magellan.Exceptions.MagellanProcessingException"></exception>
        private void ExportSpectraToMzMl(IEnumerable<ISpectrumDescriptor> spectrumDescriptorsGroupByFileId, string spectrum_export_file_name)
        {
            var timer = Stopwatch.StartNew();

            // Get the unique spectrum identifier from each spectrum descriptor
            var spectrum_ids = spectrumDescriptorsGroupByFileId
                .OrderBy(o => o.Header.RetentionTimeCenter)
                .Select(s => s.Header.SpectrumID)
                .ToList();

            SendAndLogTemporaryMessage("Start export of {0} spectra ...", spectrum_ids.Count);

            var exporter = new mzML
            {
                SoftwareName = "Proteome Discoverer",
                SoftwareVersion = new Version(FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly().Location).FileVersion)
            };

            bool export_file_is_open = exporter.Open(spectrum_export_file_name, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);

            if (!export_file_is_open)
            {
                throw new MagellanProcessingException(String.Format("Cannot create or open mzML file: {0}", spectrum_export_file_name));
            }

            // Retrieve spectra in bunches from the spectrum cache and export themto the new created mzML file.			
            var spectra = new MassSpectrumCollection(1000);

            foreach (var spectrum in ProcessingServices.SpectrumProcessingService.ReadSpectraFromCache(spectrum_ids))
            {
                spectra.Add(spectrum);

                if (spectra.Count == 1000)
                {
                    exporter.ExportMassSpectra(spectra);
                    spectra.Clear();
                }
            }

            exporter.ExportMassSpectra(spectra);

            exporter.Close();

            SendAndLogMessage("Exporting {0} spectra took {1}.", spectrum_ids.Count, StringHelper.GetDisplayString(timer.Elapsed));
        }

        #endregion

        /// <summary>
        /// Creates database indices.
        /// </summary>
        private void AddDatabaseIndices()
        {
            EntityDataService.CreateIndex<MassSpectrumItem>();
        }

        /// <summary>
        /// Executes the pipeline.
        /// </summary>
        /// <exception cref="Thermo.Magellan.Exceptions.MagellanProcessingException"></exception>
        private void RunOpenMsPipeline(List<string> spectrumExportFileNames)
        {
            //check that entries in list of filenames  is ok
            foreach (var fn in spectrumExportFileNames)
            {
                ArgumentHelper.AssertStringNotNullOrWhitespace(fn, "spectrumExportFileName");
            }
            var timer = Stopwatch.StartNew();
            SendAndLogMessage("Starting OpenMS pipeline to process spectra ...");

            //initialise variables
            string mass_error = param_mass_tolerance.ToString(); //MassError obtained from workflow option
            mass_error = mass_error.Substring(0, mass_error.Length - 4); //remove ' ppm' part (ppm is enforced)            

            //list of input and output files of specific OpenMS tools
            string[] in_files = new string[m_num_files];
            string[] out_files = new string[m_num_files];
            string ini_path = ""; //path to configuration files with parameters for the OpenMS Tool

            //create Lists of possible OpenMS files
            List<string> featurexml_out_files = new List<string>(m_num_files);

            //Add path of Open MS installation here
            var openms_dir = Path.Combine(ServerConfiguration.ToolsDirectory, "OpenMS-2.0/");

            //Feature detection, do once for each exported file
            var exec_path = Path.Combine(openms_dir, @"bin/FeatureFinderMultiplex.exe");
            for (int i = 0; i < m_num_files; i++)
            {
                in_files[i] = spectrumExportFileNames[i];
                out_files[i] = Path.Combine(Path.GetDirectoryName(EntityDataService.ReportFile.FileName),
                                            Path.GetFileNameWithoutExtension(in_files[i])) + ".featureXML";

                featurexml_out_files.Add(out_files[i]);

                ini_path = Path.Combine(NodeScratchDirectory, @"FeatureFinderMultiplex.ini");
                OpenMSCommons.CreateDefaultINI(exec_path, ini_path, NodeScratchDirectory, new NodeLoggerErrorDelegate(NodeLogger.ErrorFormat), new NodeLoggerWarningDelegate(NodeLogger.WarnFormat));

                Dictionary<string, string> ff_parameters = new Dictionary<string, string> {
                            {"in", in_files[i]},
                            {"out_features", out_files[i]},
                            {"labels", ""},
                            {"averagine_similarity", param_averagine_similarity.ToString()},
                            {"charge", param_charge_low.ToString() + ":" + param_charge_high.ToString()},
                            {"mz_unit", param_mass_tolerance.UnitToString()},
                            {"mz_tolerance", param_mass_tolerance.Value.Tolerance.ToString()},
                            {"rt_typical", param_typical_rt.ToString()},
                            {"rt_min", param_minimum_rt.ToString()}
                };

                OpenMSCommons.WriteParamsToINI(ini_path, ff_parameters);

                SendAndLogMessage("Starting FeatureFinderMultiplex for file [{0}]", in_files[i]);
                OpenMSCommons.RunTOPPTool(exec_path,
                                      ini_path,
                                      NodeScratchDirectory,
                                      new SendAndLogMessageDelegate(SendAndLogMessage),
                                      new SendAndLogTemporaryMessageDelegate(SendAndLogTemporaryMessage),
                                      new WriteLogMessageDelegate(WriteLogMessage),
                                      new NodeLoggerWarningDelegate(NodeLogger.WarnFormat),
                                      new NodeLoggerErrorDelegate(NodeLogger.ErrorFormat));

                m_current_step += 1;
                ReportTotalProgress((double)m_current_step / m_num_steps);
            }

            // save featureXML file names to msf file
            var featurexml_field = ProcessingServices.CustomDataService.GetOrCreateCustomDataField(WorkflowID, new Guid("BEC3E6A6-51CB-4FBB-A579-34312EA78C05"), "FileNames", ProcessingNodeNumber, ProcessingNodeNumber, CustomDataTarget.ProcessingNode, CustomDataType.String, CustomDataAccessMode.Read, DataVisibility.Hidden, dataPurpose: "FeatureXmlFiles");
            ProcessingServices.CustomDataService.WriteString(featurexml_field, ProcessingNodeNumber, string.Join(",", out_files.ToArray()));

            SendAndLogMessage("OpenMS pipeline processing took {0}.", StringHelper.GetDisplayString(timer.Elapsed));
        }

        /// <summary>
        /// Stores information about the used entity object types and connections.
        /// </summary>
        private void RegisterEntityObjectTypes()
        {
            // register items
            EntityDataService.RegisterEntity<MassSpectrumItem>(ProcessingNodeNumber);
        }
    }
}

