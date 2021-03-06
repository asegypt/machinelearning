﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.ML.Core.Data;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.StaticPipe;
using Microsoft.ML.StaticPipe.Runtime;
using Microsoft.ML.Transforms;
using Microsoft.ML.Transforms.TensorFlow;

[assembly: LoadableClass(TensorFlowTransform.Summary, typeof(IDataTransform), typeof(TensorFlowTransform),
    typeof(TensorFlowTransform.Arguments), typeof(SignatureDataTransform), TensorFlowTransform.UserName, TensorFlowTransform.ShortName)]

[assembly: LoadableClass(TensorFlowTransform.Summary, typeof(IDataTransform), typeof(TensorFlowTransform), null, typeof(SignatureLoadDataTransform),
    TensorFlowTransform.UserName, TensorFlowTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(TensorFlowTransform), null, typeof(SignatureLoadModel),
    TensorFlowTransform.UserName, TensorFlowTransform.LoaderSignature)]

[assembly: LoadableClass(typeof(IRowMapper), typeof(TensorFlowTransform), null, typeof(SignatureLoadRowMapper),
    TensorFlowTransform.UserName, TensorFlowTransform.LoaderSignature)]

[assembly: EntryPointModule(typeof(TensorFlowTransform))]

namespace Microsoft.ML.Transforms
{
    /// <include file='doc.xml' path='doc/members/member[@name="TensorflowTransform"]/*' />
    public sealed class TensorFlowTransform : RowToRowTransformerBase
    {
        public sealed class Arguments : TransformInputBase
        {
            /// <summary>
            /// Location of the TensorFlow model.
            /// </summary>
            [Argument(ArgumentType.Required, HelpText = "TensorFlow model used by the transform. Please see https://www.tensorflow.org/mobile/prepare_models for more details.", SortOrder = 0)]
            public string ModelLocation;

            /// <summary>
            /// The names of the model inputs.
            /// </summary>
            [Argument(ArgumentType.Multiple | ArgumentType.Required, HelpText = "The names of the model inputs", ShortName = "inputs", SortOrder = 1)]
            public string[] InputColumns;

            /// <summary>
            /// The names of the requested model outputs.
            /// </summary>
            [Argument(ArgumentType.Multiple | ArgumentType.Required, HelpText = "The name of the outputs", ShortName = "outputs", SortOrder = 2)]
            public string[] OutputColumns;

            /// <summary>
            /// The name of the label column in <see cref="IDataView"/> that will be mapped to label node in TensorFlow model.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Training labels.", ShortName = "label", SortOrder = 4)]
            public string LabelColumn;

            /// <summary>
            /// The name of the label in TensorFlow model.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "TensorFlow label node.", ShortName = "TFLabel", SortOrder = 5)]
            public string TensorFlowLabel;

            /// <summary>
            /// Name of the operation in TensorFlow graph that is used for optimizing parameters in the graph.
            /// Usually it is the name specified in the minimize method of optimizer in python
            /// e.g. optimizer = tf.train.GradientDescentOptimizer(learning_rate).minimize(cost, name = "SGDOptimizer").
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "The name of the optimization operation in the TensorFlow graph.", ShortName = "OptimizationOp", SortOrder = 6)]
            public string OptimizationOperation;

            /// <summary>
            /// The name of the operation in the TensorFlow graph to compute training loss (Optional).
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "The name of the operation in the TensorFlow graph to compute training loss (Optional)", ShortName = "LossOp", SortOrder = 7)]
            public string LossOperation;

            /// <summary>
            /// The name of the operation in the TensorFlow graph to compute performance metric during training (Optional).
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "The name of the operation in the TensorFlow graph to compute performance metric during training (Optional)", ShortName = "MetricOp", SortOrder = 8)]
            public string MetricOperation;

            /// <summary>
            /// Number of samples to use for mini-batch training.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Number of samples to use for mini-batch training.", SortOrder = 9)]
            public int BatchSize = 64;

            /// <summary>
            /// Number of training iterations.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Number of training iterations.", SortOrder = 10)]
            public int Epoch = 5;

            /// <summary>
            /// The name of the operation in the TensorFlow graph which sets optimizer learning rate (Optional).
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "The name of the operation in the TensorFlow graph which sets optimizer learning rate (Optional).", SortOrder = 11)]
            public string LearningRateOperation;

            /// <summary>
            /// Learning rate to use during optimization.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Learning rate to use during optimization.", SortOrder = 12)]
            public float LearningRate = 0.01f;

            /// <summary>
            /// Name of the input in TensorFlow graph that specifiy the location for saving/restoring models to/from disk.
            /// This parameter is set by different kinds of 'Savers' in TensorFlow and users don't have control over this.
            /// Therefore, its highly unlikely that this parameter is changed from its default value of 'save/Const'.
            /// Please change it cautiously if you need to.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Name of the input in TensorFlow graph that specifiy the location for saving/restoring models from disk.", SortOrder = 13)]
            public string SaveLocationOperation = "save/Const";

            /// <summary>
            /// Name of the operation in TensorFlow graph that is used for saving/restoring models to/from disk.
            /// This parameter is set by different kinds of 'Savers' in TensorFlow and users don't have control over this.
            /// Therefore, its highly unlikely that this parameter is changed from its default value of 'save/control_dependency'.
            /// Please change it cautiously if you need to.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Name of the input in TensorFlow graph that specifiy the location for saving/restoring models from disk.", SortOrder = 14)]
            public string SaveOperation = "save/control_dependency";

            /// <summary>
            /// Needed for command line to specify if retraining is requested.
            /// </summary>
            [Argument(ArgumentType.AtMostOnce, HelpText = "Retrain TensorFlow model.", SortOrder = 15)]
            public bool ReTrain = false;
        }

        private readonly string _savedModelPath;
        private readonly bool _isTemporarySavedModel;

        internal readonly TFSession Session;
        internal readonly ColumnType[] OutputTypes;
        internal readonly TFDataType[] TFOutputTypes;
        internal readonly TFDataType[] TFInputTypes;
        internal readonly TFShape[] TFInputShapes;
        internal TFGraph Graph => Session.Graph;

        public readonly string[] Inputs;
        public readonly string[] Outputs;

        public static int BatchSize = 1;
        internal const string Summary = "Transforms the data using the TensorFlow model.";
        internal const string UserName = "TensorFlowTransform";
        internal const string ShortName = "TFTransform";
        internal const string LoaderSignature = "TensorFlowTransform";

        internal static class DefaultModelFileNames
        {
            public const string VariablesFolder = "variables";
            public const string Index = "variables.index";
            public const string Data = "variables.data-00000-of-00001";
            public const string Graph = "saved_model.pb";
            public const string TmpMlnetModel = "mlnet_model";
        }

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "TENSFLOW",
                //verWrittenCur: 0x00010001, // Initial
                verWrittenCur: 0x00010002,  // Added Support for Multiple Outputs and SavedModel.
                verReadableCur: 0x00010002,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(TensorFlowTransform).Assembly.FullName);
        }

        /// <summary>
        /// Creates <see cref="IDataTransform"/> using <see cref="TensorFlowTransform"/>.
        /// This convenience method get the model file as input and loads the model internally.
        /// If the model is already loaded please <see cref="TensorFlowTransform.Create(IHostEnvironment, IDataView, TensorFlowModelInfo, string[], string[])"/> to avoid reloading of model.
        /// </summary>
        /// <param name="env">Host Environment.</param>
        /// <param name="input">Input <see cref="IDataView"/>. This is the output from previous transform or loader.</param>
        /// <param name="model">Path to the TensorFlow model. </param>
        /// <param name="names">Name of the output column(s). Keep it same as in the Tensorflow model.</param>
        /// <param name="source">Name of the input column(s). Keep it same as in the Tensorflow model.</param>
        public static IDataTransform Create(IHostEnvironment env, IDataView input, string model, string[] names, string[] source)
        {
            return new TensorFlowTransform(env, TensorFlowUtils.GetSession(env, model), source, names, TensorFlowUtils.IsSavedModel(env, model) ? model : null, false).MakeDataTransform(input);
        }

        /// <summary>
        /// Creates <see cref="IDataTransform"/> using <see cref="TensorFlowTransform"/>.
        /// This convenience method avoids reloading of TensorFlow model.
        /// It is useful in a situation where user has already loaded TensorFlow model using <see cref="TensorFlowUtils.LoadTensorFlowModel(IHostEnvironment, string)"/> for inspecting model schema.
        /// </summary>
        /// <param name="env">Host Environment.</param>
        /// <param name="input">Input <see cref="IDataView"/>. This is the output from previous transform or loader.</param>
        /// <param name="tfModelInfo"> <see cref="TensorFlowModelInfo"/> object created with <see cref="TensorFlowUtils.LoadTensorFlowModel(IHostEnvironment, string)"/>.</param>
        /// <param name="names">Name of the output column(s). Keep it same as in the Tensorflow model.</param>
        /// <param name="source">Name of the input column(s). Keep it same as in the Tensorflow model.</param>
        public static IDataTransform Create(IHostEnvironment env, IDataView input, TensorFlowModelInfo tfModelInfo, string[] names, string[] source)
        {
            return new TensorFlowTransform(env, tfModelInfo.Session, source, names, TensorFlowUtils.IsSavedModel(env, tfModelInfo.ModelPath) ? tfModelInfo.ModelPath : null, false).MakeDataTransform(input);
        }

        // Factory method for SignatureLoadModel.
        private static TensorFlowTransform Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());

            // *** Binary format ***
            // byte: indicator for frozen models
            // stream: tensorFlow model.
            // int: number of input columns
            // for each input column
            //   int: id of int column name
            // int: number of output columns
            // for each output column
            //   int: id of output column name
            GetModelInfo(env, ctx, out string[] inputs, out string[] outputs, out bool isFrozen);
            if (isFrozen)
            {
                byte[] modelBytes = null;
                if (!ctx.TryLoadBinaryStream("TFModel", r => modelBytes = r.ReadByteArray()))
                    throw env.ExceptDecode();
                return new TensorFlowTransform(env, TensorFlowUtils.LoadTFSession(env, modelBytes), inputs, outputs, null, false);
            }

            var tempDirPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), nameof(TensorFlowTransform) + "_" + Guid.NewGuid()));
            TensorFlowUtils.CreateFolderWithAclIfNotExists(env, tempDirPath);
            try
            {
                var load = ctx.TryLoadBinaryStream("TFSavedModel", br =>
                {
                    int count = br.ReadInt32();
                    for (int n = 0; n < count; n++)
                    {
                        string relativeFile = br.ReadString();
                        long fileLength = br.ReadInt64();

                        string fullFilePath = Path.Combine(tempDirPath, relativeFile);
                        string fullFileDir = Path.GetDirectoryName(fullFilePath);
                        if (fullFileDir != tempDirPath)
                        {
                            TensorFlowUtils.CreateFolderWithAclIfNotExists(env, fullFileDir);
                        }
                        using (var fs = new FileStream(fullFilePath, FileMode.Create, FileAccess.Write))
                        {
                            long actualRead = br.BaseStream.CopyRange(fs, fileLength);
                            env.Assert(actualRead == fileLength);
                        }
                    }
                });

                return new TensorFlowTransform(env, TensorFlowUtils.GetSession(env, tempDirPath), inputs, outputs, tempDirPath, true);
            }
            catch (Exception)
            {
                TensorFlowUtils.DeleteFolderWithRetries(env, tempDirPath);
                throw;
            }
        }

        // Factory method for SignatureDataTransform.
        public static IDataTransform Create(IHostEnvironment env, Arguments args, IDataView input)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(args, nameof(args));
            env.CheckValue(input, nameof(input));
            env.CheckValue(args.InputColumns, nameof(args.InputColumns));
            env.CheckValue(args.OutputColumns, nameof(args.OutputColumns));

            return new TensorFlowTransform(env, args, input).MakeDataTransform(input);
        }

        internal TensorFlowTransform(IHostEnvironment env, Arguments args, IDataView input)
            : this(env, args, TensorFlowUtils.LoadTensorFlowModel(env, args.ModelLocation), input)
        {
        }

        internal TensorFlowTransform(IHostEnvironment env, Arguments args, TensorFlowModelInfo tensorFlowModel, IDataView input)
            : this(env, tensorFlowModel.Session, args.InputColumns, args.OutputColumns, TensorFlowUtils.IsSavedModel(env, args.ModelLocation) ? args.ModelLocation : null, false)
        {

            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(args, nameof(args));

            if (args.ReTrain)
            {
                env.CheckValue(input, nameof(input));

                CheckTrainingParameters(args);

                if (!TensorFlowUtils.IsSavedModel(env, args.ModelLocation))
                    throw env.ExceptNotSupp("TensorFlowTransform: Re-Training of TensorFlow model is only supported for un-frozen model.");
                TrainCore(args, input);
            }
        }

        private void CheckTrainingParameters(Arguments args)
        {
            Host.CheckNonWhiteSpace(args.LabelColumn, nameof(args.LabelColumn));
            Host.CheckNonWhiteSpace(args.OptimizationOperation, nameof(args.OptimizationOperation));
            if (Session.Graph[args.OptimizationOperation] == null)
                throw Host.ExceptParam(nameof(args.OptimizationOperation), $"Optimization operation '{args.OptimizationOperation}' does not exist in the model");

            Host.CheckNonWhiteSpace(args.TensorFlowLabel, nameof(args.TensorFlowLabel));
            if (Session.Graph[args.TensorFlowLabel] == null)
                throw Host.ExceptParam(nameof(args.TensorFlowLabel), $"'{args.TensorFlowLabel}' does not exist in the model");

            Host.CheckNonWhiteSpace(args.SaveLocationOperation, nameof(args.SaveLocationOperation));
            if (Session.Graph[args.SaveLocationOperation] == null)
                throw Host.ExceptParam(nameof(args.SaveLocationOperation), $"'{args.SaveLocationOperation}' does not exist in the model");

            Host.CheckNonWhiteSpace(args.SaveOperation, nameof(args.SaveOperation));
            if (Session.Graph[args.SaveOperation] == null)
                throw Host.ExceptParam(nameof(args.SaveOperation), $"'{args.SaveOperation}' does not exist in the model");

            if (args.LossOperation != null)
            {
                Host.CheckNonWhiteSpace(args.LossOperation, nameof(args.LossOperation));
                if (Session.Graph[args.LossOperation] == null)
                    throw Host.ExceptParam(nameof(args.LossOperation), $"'{args.LossOperation}' does not exist in the model");
            }

            if (args.MetricOperation != null)
            {
                Host.CheckNonWhiteSpace(args.MetricOperation, nameof(args.MetricOperation));
                if (Session.Graph[args.MetricOperation] == null)
                    throw Host.ExceptParam(nameof(args.MetricOperation), $"'{args.MetricOperation}' does not exist in the model");
            }

            if (args.LearningRateOperation != null)
            {
                Host.CheckNonWhiteSpace(args.LearningRateOperation, nameof(args.LearningRateOperation));
                if (Session.Graph[args.LearningRateOperation] == null)
                    throw Host.ExceptParam(nameof(args.LearningRateOperation), $"'{args.LearningRateOperation}' does not exist in the model");
            }
        }

        private (int, bool, TFDataType, TFShape) GetTrainingInputInfo(ISchema inputSchema, string columnName, string tfNodeName, int batchSize)
        {
            if (!inputSchema.TryGetColumnIndex(columnName, out int inputColIndex))
                throw Host.Except($"Column {columnName} doesn't exist");

            var type = inputSchema.GetColumnType(inputColIndex);
            var isInputVector = type.IsVector;

            var tfInput = new TFOutput(Graph[tfNodeName]);
            var tfInputType = tfInput.OutputType;
            var tfInputShape = Graph.GetTensorShape(tfInput);
            if (tfInputShape.NumDimensions != -1)
            {
                var newShape = new long[tfInputShape.NumDimensions];
                newShape[0] = tfInputShape[0] == -1 ? batchSize : tfInputShape[0];

                for (int j = 1; j < tfInputShape.NumDimensions; j++)
                    newShape[j] = tfInputShape[j];
                tfInputShape = new TFShape(newShape);
            }

            var expectedType = TensorFlowUtils.Tf2MlNetType(tfInputType);
            if (type.ItemType != expectedType)
                throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", columnName, expectedType.ToString(), type.ToString());

            return (inputColIndex, isInputVector, tfInputType, tfInputShape);
        }

        private void TrainCore(Arguments args, IDataView input)
        {
            var inputsForTraining = new string[Inputs.Length + 1];
            var inputColIndices = new int[inputsForTraining.Length];
            var isInputVector = new bool[inputsForTraining.Length];
            var tfInputTypes = new TFDataType[inputsForTraining.Length];
            var tfInputShapes = new TFShape[inputsForTraining.Length];

            for (int i = 0; i < Inputs.Length; i++)
            {
                inputsForTraining[i] = Inputs[i];
            }

            var inputSchema = input.Schema;
            for (int i = 0; i < inputsForTraining.Length - 1; i++)
            {
                (inputColIndices[i], isInputVector[i], tfInputTypes[i], tfInputShapes[i]) =
                    GetTrainingInputInfo(inputSchema, inputsForTraining[i], inputsForTraining[i], args.BatchSize);
            }

            var index = inputsForTraining.Length - 1;
            inputsForTraining[index] = args.TensorFlowLabel;
            (inputColIndices[index], isInputVector[index], tfInputTypes[index], tfInputShapes[index]) =
                    GetTrainingInputInfo(inputSchema, args.LabelColumn, inputsForTraining[index], args.BatchSize);

            var fetchList = new List<string>();
            if (args.LossOperation != null)
                fetchList.Add(args.LossOperation);
            if (args.MetricOperation != null)
                fetchList.Add(args.MetricOperation);

            var hashedIndices = new HashSet<int>(inputColIndices);
            for (int epoch = 0; epoch < args.Epoch; epoch++)
            {
                using (var cursor = input.GetRowCursor(a => hashedIndices.Contains(a)))
                {
                    var srcTensorGetters = GetTensorValueGetters(cursor, inputColIndices, isInputVector, tfInputTypes, tfInputShapes);

                    float loss = 0;
                    float metric = 0;
                    bool isDataLeft = false;
                    using (var ch = Host.Start("Training TensorFlow model..."))
                    using (var pch = Host.StartProgressChannel("TensorFlow training progress..."))
                    {
                        pch.SetHeader(new ProgressHeader(new[] { "Loss", "Metric" }, new[] { "Epoch" }), (e) => e.SetProgress(0, epoch, args.Epoch));

                        while (cursor.MoveNext())
                        {
                            for (int i = 0; i < inputColIndices.Length; i++)
                            {
                                isDataLeft = true;
                                srcTensorGetters[i].BufferTrainingData();
                            }

                            if (((cursor.Position + 1) % args.BatchSize) == 0)
                            {
                                isDataLeft = false;
                                var (l, m) = TrainBatch(inputColIndices, inputsForTraining, srcTensorGetters, fetchList, args);
                                loss += l;
                                metric += m;
                            }
                        }
                        if (isDataLeft)
                        {
                            isDataLeft = false;
                            ch.Warning("Not training on the last batch. The batch size is less than {0}.", args.BatchSize);
                        }
                        pch.Checkpoint(new double?[] { loss, metric });
                    }
                }
            }
            UpdateModelOnDisk(args.ModelLocation, args);
        }

        private (float loss, float metric) TrainBatch(int[] inputColIndices,
            string[] inputsForTraining,
            ITensorValueGetter[] srcTensorGetters,
            List<string> fetchList,
            Arguments args)
        {
            float loss = 0;
            float metric = 0;
            var runner = Session.GetRunner();
            for (int i = 0; i < inputColIndices.Length; i++)
            {
                var inputName = inputsForTraining[i];
                runner.AddInput(inputName, srcTensorGetters[i].GetBufferedBatchTensor());
            }

            if (args.LearningRateOperation != null)
                runner.AddInput(args.LearningRateOperation, new TFTensor(args.LearningRate));
            runner.AddTarget(args.OptimizationOperation);

            if (fetchList.Count > 0)
                runner.Fetch(fetchList.ToArray());

            var tensor = runner.Run();
            loss = tensor.Length > 0 ? (float)tensor[0].GetValue() : 0.0f;
            metric = tensor.Length > 1 ? (float)tensor[1].GetValue() : 0.0f;

            return (loss, metric);
        }

        /// <summary>
        /// Updates the model on the disk.
        /// After retraining Session and Graphs are both up-to-date
        /// However model on disk is not which is used to serialzed to ML.Net stream
        /// </summary>
        private void UpdateModelOnDisk(string modelDir, Arguments args)
        {
            try
            {
                // Save the model on disk
                var path = Path.Combine(modelDir, DefaultModelFileNames.TmpMlnetModel);
                Session.GetRunner().AddInput(args.SaveLocationOperation, TFTensor.CreateString(Encoding.UTF8.GetBytes(path)))
                        .AddTarget(args.SaveOperation).Run();

                // Preserve original files
                var variablesPath = Path.Combine(modelDir, DefaultModelFileNames.VariablesFolder);
                var archivePath = Path.Combine(variablesPath + "-" + Guid.NewGuid().ToString());
                Directory.CreateDirectory(archivePath);
                foreach (var f in Directory.GetFiles(variablesPath))
                    File.Copy(f, Path.Combine(archivePath, Path.GetFileName(f)));

                string[] modelFilePaths = null;

                // There are two ways parameters are saved depending on
                // either `saver_def = tf.train.Saver().as_saver_def()` was called in Python before `tf.saved_model.simple_save` or not.
                // If `saver_def = tf.train.Saver().as_saver_def()` was called files are saved in top directory.
                // If not then temporary directory is created in current directory which starts with `mlnet_model`
                // and files are saved there.
                var tmpParamDir = Directory.GetDirectories(modelDir, DefaultModelFileNames.TmpMlnetModel + "*");
                if (tmpParamDir != null && tmpParamDir.Length > 0)
                    modelFilePaths = Directory.GetFiles(tmpParamDir[0]);
                else
                    modelFilePaths = Directory.GetFiles(modelDir, DefaultModelFileNames.TmpMlnetModel + "*");

                foreach (var file in modelFilePaths)
                {
                    if (file.EndsWith(".data-00000-of-00001"))
                    {
                        var destination = Path.Combine(variablesPath, DefaultModelFileNames.Data);
                        if (File.Exists(destination))
                            File.Delete(destination);
                        Directory.Move(file, destination);
                    }
                    if (file.EndsWith(".index"))
                    {
                        var destination = Path.Combine(variablesPath, DefaultModelFileNames.Index);
                        if (File.Exists(destination))
                            File.Delete(destination);
                        Directory.Move(file, destination);
                    }
                }

                if (tmpParamDir != null && tmpParamDir.Length > 0)
                    TensorFlowUtils.DeleteFolderWithRetries(Host, tmpParamDir[0]);
            }
            catch (Exception e)
            {
                throw Host.ExceptIO(e, "Error serializing TensorFlow retrained model to disk.");
            }
        }

        private static ITensorValueGetter CreateTensorValueGetter<T>(IRow input, bool isVector, int colIndex, TFShape tfShape)
        {
            if (isVector)
                return new TensorValueGetterVec<T>(input, colIndex, tfShape);
            return new TensorValueGetter<T>(input, colIndex, tfShape);
        }

        private static ITensorValueGetter CreateTensorValueGetter(IRow input, TFDataType tfType, bool isVector, int colIndex, TFShape tfShape)
        {
            var type = TFTensor.TypeFromTensorType(tfType);
            Contracts.AssertValue(type);
            return Utils.MarshalInvoke(CreateTensorValueGetter<int>, type, input, isVector, colIndex, tfShape);
        }

        private static ITensorValueGetter[] GetTensorValueGetters(IRow input,
            int[] inputColIndices,
            bool[] isInputVector,
            TFDataType[] tfInputTypes,
            TFShape[] tfInputShapes)
        {
            var srcTensorGetters = new ITensorValueGetter[inputColIndices.Length];
            for (int i = 0; i < inputColIndices.Length; i++)
            {
                int colIndex = inputColIndices[i];
                srcTensorGetters[i] = CreateTensorValueGetter(input, tfInputTypes[i], isInputVector[i], colIndex, tfInputShapes[i]);
            }
            return srcTensorGetters;
        }

        // Factory method for SignatureLoadDataTransform.
        private static IDataTransform Create(IHostEnvironment env, ModelLoadContext ctx, IDataView input)
            => Create(env, ctx).MakeDataTransform(input);

        // Factory method for SignatureLoadRowMapper.
        private static IRowMapper Create(IHostEnvironment env, ModelLoadContext ctx, ISchema inputSchema)
            => Create(env, ctx).MakeRowMapper(Schema.Create(inputSchema));

        private static void GetModelInfo(IHostEnvironment env, ModelLoadContext ctx, out string[] inputs, out string[] outputs, out bool isFrozen)
        {
            isFrozen = true;
            bool isNonFrozenModelSupported = ctx.Header.ModelVerReadable >= 0x00010002;
            if (isNonFrozenModelSupported)
                isFrozen = ctx.Reader.ReadBoolByte();

            var numInputs = ctx.Reader.ReadInt32();
            env.CheckDecode(numInputs > 0);
            inputs = new string[numInputs];
            for (int j = 0; j < inputs.Length; j++)
                inputs[j] = ctx.LoadNonEmptyString();

            bool isMultiOutput = ctx.Header.ModelVerReadable >= 0x00010002;
            var numOutputs = 1;
            if (isMultiOutput)
                numOutputs = ctx.Reader.ReadInt32();

            env.CheckDecode(numOutputs > 0);
            outputs = new string[numOutputs];
            for (int j = 0; j < outputs.Length; j++)
                outputs[j] = ctx.LoadNonEmptyString();
        }

        internal TensorFlowTransform(IHostEnvironment env, TFSession session, string[] inputs, string[] outputs, string savedModelPath, bool isTemporarySavedModel) :
            base(Contracts.CheckRef(env, nameof(env)).Register(nameof(TensorFlowTransform)))

        {
            Host.CheckValue(session, nameof(session));
            Host.CheckNonEmpty(inputs, nameof(inputs));
            Host.CheckNonEmpty(outputs, nameof(outputs));

            Session = session;
            _savedModelPath = savedModelPath;
            _isTemporarySavedModel = isTemporarySavedModel;
            Inputs = inputs;
            Outputs = outputs;

            (TFInputTypes, TFInputShapes) = GetInputInfo(Host, Session, Inputs);
            (TFOutputTypes, OutputTypes) = GetOutputInfo(Host, Session, Outputs);
        }

        internal static (TFDataType[] tfInputTypes, TFShape[] tfInputShapes) GetInputInfo(IHost host, TFSession session, string[] inputs)
        {
            var tfInputTypes = new TFDataType[inputs.Length];
            var tfInputShapes = new TFShape[inputs.Length];

            foreach (var input in inputs)
            {
                host.CheckNonWhiteSpace(input, nameof(inputs));
                if (session.Graph[input] == null)
                    throw host.ExceptParam(nameof(inputs), $"Input column '{input}' does not exist in the model");
                var tfInput = new TFOutput(session.Graph[input]);
                if (!TensorFlowUtils.IsTypeSupported(tfInput.OutputType))
                    throw host.ExceptParam(nameof(session), $"Input type '{tfInput.OutputType}' of input column '{input}' is not supported in TensorFlow");
            }

            for (int i = 0; i < inputs.Length; i++)
            {
                var tfInput = new TFOutput(session.Graph[inputs[i]]);
                tfInputTypes[i] = tfInput.OutputType;
                tfInputShapes[i] = session.Graph.GetTensorShape(tfInput);
                if (tfInputShapes[i].NumDimensions != -1)
                {
                    var newShape = new long[tfInputShapes[i].NumDimensions];
                    newShape[0] = tfInputShapes[i][0] == -1 ? BatchSize : tfInputShapes[i][0];

                    for (int j = 1; j < tfInputShapes[i].NumDimensions; j++)
                        newShape[j] = tfInputShapes[i][j];
                    tfInputShapes[i] = new TFShape(newShape);
                }
            }
            return (tfInputTypes, tfInputShapes);
        }

        internal static (TFDataType[] tfOutputTypes, ColumnType[] outputTypes) GetOutputInfo(IHost host, TFSession session, string[] outputs)
        {
            var tfOutputTypes = new TFDataType[outputs.Length];
            var outputTypes = new ColumnType[outputs.Length];
            var newNames = new HashSet<string>();
            foreach (var output in outputs)
            {
                host.CheckNonWhiteSpace(output, nameof(outputs));
                if (!newNames.Add(output))
                    throw host.ExceptParam(nameof(outputs), $"Output column '{output}' specified multiple times");
                if (session.Graph[output] == null)
                    throw host.ExceptParam(nameof(outputs), $"Output column '{output}' does not exist in the model");
            }

            for (int i = 0; i < outputs.Length; i++)
            {
                var tfOutput = new TFOutput(session.Graph[outputs[i]]);
                var shape = session.Graph.GetTensorShape(tfOutput);
                int[] dims = shape.NumDimensions > 0 ? shape.ToIntArray().Skip(shape[0] == -1 ? 1 : 0).ToArray() : new[] { 0 };
                var type = TensorFlowUtils.Tf2MlNetType(tfOutput.OutputType);
                outputTypes[i] = new VectorType(type, dims);
                tfOutputTypes[i] = tfOutput.OutputType;
            }

            return (tfOutputTypes, outputTypes);
        }

        protected override IRowMapper MakeRowMapper(Schema inputSchema) => new Mapper(this, inputSchema);

        public override void Save(ModelSaveContext ctx)
        {
            Host.AssertValue(ctx);
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // byte: indicator for frozen models
            // stream: tensorFlow model.
            // int: number of input columns
            // for each input column
            //   int: id of int column name
            // int: number of output columns
            // for each output column
            //   int: id of output column name
            var isFrozen = string.IsNullOrEmpty(_savedModelPath);
            ctx.Writer.WriteBoolByte(isFrozen);
            if (isFrozen)
            {
                var buffer = new TFBuffer();
                Session.Graph.ToGraphDef(buffer);
                ctx.SaveBinaryStream("TFModel", w =>
                {
                    w.WriteByteArray(buffer.ToSpan());
                });
            }
            else
            {
                ctx.SaveBinaryStream("TFSavedModel", w =>
                {
                    // only these files need to be saved.
                    string[] modelFilePaths =
                    {
                        Path.Combine(_savedModelPath, DefaultModelFileNames.Graph),
                        Path.Combine(_savedModelPath, DefaultModelFileNames.VariablesFolder, DefaultModelFileNames.Data),
                        Path.Combine(_savedModelPath, DefaultModelFileNames.VariablesFolder, DefaultModelFileNames.Index),
                    };

                    w.Write(modelFilePaths.Length);

                    foreach (var fullPath in modelFilePaths)
                    {
                        var relativePath = fullPath.Substring(_savedModelPath.Length + 1);
                        w.Write(relativePath);

                        using (var fs = new FileStream(fullPath, FileMode.Open))
                        {
                            long fileLength = fs.Length;
                            w.Write(fileLength);
                            long actualWritten = fs.CopyRange(w.BaseStream, fileLength);
                            Host.Assert(actualWritten == fileLength);
                        }
                    }
                });
            }
            Host.AssertNonEmpty(Inputs);
            ctx.Writer.Write(Inputs.Length);
            foreach (var colName in Inputs)
                ctx.SaveNonEmptyString(colName);

            Host.AssertNonEmpty(Outputs);
            ctx.Writer.Write(Outputs.Length);
            foreach (var colName in Outputs)
                ctx.SaveNonEmptyString(colName);
        }

        ~TensorFlowTransform()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            // Ensure that the Session is not null and it's handle is not Zero, as it may have already been disposed/finalized.
            // Technically we shouldn't be calling this if disposing == false, since we're running in finalizer
            // and the GC doesn't guarantee ordering of finalization of managed objects, but we have to make sure
            // that the Session is closed before deleting our temporary directory.
            try
            {
                if (Session?.Handle != IntPtr.Zero)
                {
                    Session.CloseSession();
                    Session.Dispose();
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(_savedModelPath) && _isTemporarySavedModel)
                {
                    TensorFlowUtils.DeleteFolderWithRetries(Host, _savedModelPath);
                }
            }
        }

        private sealed class Mapper : MapperBase
        {
            private readonly TensorFlowTransform _parent;
            private readonly int[] _inputColIndices;
            private readonly bool[] _isInputVector;
            private readonly TFShape[] _fullySpecifiedShapes;

            public Mapper(TensorFlowTransform parent, Schema inputSchema) :
                   base(Contracts.CheckRef(parent, nameof(parent)).Host.Register(nameof(Mapper)), inputSchema)
            {
                Host.CheckValue(parent, nameof(parent));
                _parent = parent;
                _inputColIndices = new int[_parent.Inputs.Length];
                _isInputVector = new bool[_parent.Inputs.Length];
                _fullySpecifiedShapes = new TFShape[_parent.Inputs.Length];
                for (int i = 0; i < _parent.Inputs.Length; i++)
                {
                    if (!inputSchema.TryGetColumnIndex(_parent.Inputs[i], out _inputColIndices[i]))
                        throw Host.Except($"Column {_parent.Inputs[i]} doesn't exist");

                    var type = inputSchema.GetColumnType(_inputColIndices[i]);
                    if (type is VectorType vecType && vecType.Size == 0)
                        throw Host.Except("Variable length input columns not supported");

                    _isInputVector[i] = type is VectorType;
                    if (!_isInputVector[i]) // Temporary pending fix of issue #1542. In its current state, the below code would fail anyway with a naked exception if this check was not here.
                        throw Host.Except("Non-vector columns not supported");
                    vecType = (VectorType)type;
                    var expectedType = TensorFlowUtils.Tf2MlNetType(_parent.TFInputTypes[i]);
                    if (type.ItemType != expectedType)
                        throw Host.ExceptSchemaMismatch(nameof(inputSchema), "input", _parent.Inputs[i], expectedType.ToString(), type.ToString());
                    var originalShape = _parent.TFInputShapes[i];
                    var shape = originalShape.ToIntArray();

                    var colTypeDims = vecType.Dimensions.Prepend(1).Select(dim => (long)dim).ToArray();
                    if (shape == null)
                        _fullySpecifiedShapes[i] = new TFShape(colTypeDims);
                    else if (vecType.Dimensions.Length == 1)
                    {
                        // If the column is one dimension we make sure that the total size of the TF shape matches.
                        // Compute the total size of the known dimensions of the shape.
                        int valCount = shape.Where(x => x > 0).Aggregate((x, y) => x * y);
                        // The column length should be divisible by this, so that the other dimensions can be integral.
                        if (type.ValueCount % valCount != 0)
                            throw Contracts.Except($"Input shape mismatch: Input '{_parent.Inputs[i]}' has shape {originalShape.ToString()}, but input data is of length {type.ValueCount}.");

                        // If the shape is multi-dimensional, we should be able to create the length of the vector by plugging
                        // in a single value for the unknown shapes. For example, if the shape is [?,?,3], then there should exist a value
                        // d such that d*d*3 is equal to the length of the input column.
                        var d = originalShape.NumDimensions > 2 ? Math.Pow(type.ValueCount / valCount, 1.0 / (originalShape.NumDimensions - 2)) : 1;
                        if (originalShape.NumDimensions > 2 && d - (int)d != 0)
                            throw Contracts.Except($"Input shape mismatch: Input '{_parent.Inputs[i]}' has shape {originalShape.ToString()}, but input data is of length {type.ValueCount}.");

                        // Fill in the unknown dimensions.
                        var l = new long[originalShape.NumDimensions];
                        for (int ishape = 0; ishape < originalShape.NumDimensions; ishape++)
                            l[ishape] = originalShape[ishape] == -1 ? (int)d : originalShape[ishape];
                        _fullySpecifiedShapes[i] = new TFShape(l);
                    }
                    else
                    {
                        if (shape.Select((dim, j) => dim != -1 && dim != colTypeDims[j]).Any(b => b))
                            throw Contracts.Except($"Input shape mismatch: Input '{_parent.Inputs[i]}' has shape {originalShape.ToString()}, but input data is {type.AsVector.ToString()}.");

                        // Fill in the unknown dimensions.
                        var l = new long[originalShape.NumDimensions];
                        for (int ishape = 0; ishape < originalShape.NumDimensions; ishape++)
                            l[ishape] = originalShape[ishape] == -1 ? colTypeDims[ishape] : originalShape[ishape];
                        _fullySpecifiedShapes[i] = new TFShape(l);
                    }
                }
            }

            public override void Save(ModelSaveContext ctx) => _parent.Save(ctx);

            private class OutputCache
            {
                public long Position;
                public Dictionary<string, TFTensor> Outputs;
                public OutputCache()
                {
                    Position = -1;
                    Outputs = new Dictionary<string, TFTensor>();
                }
            }

            protected override Delegate MakeGetter(IRow input, int iinfo, Func<int, bool> activeOutput, out Action disposer)
            {
                disposer = null;
                Host.AssertValue(input);

                var outputCache = new OutputCache();
                var activeOutputColNames = _parent.Outputs.Where((x, i) => activeOutput(i)).ToArray();

                var type = TFTensor.TypeFromTensorType(_parent.TFOutputTypes[iinfo]);
                Host.Assert(type == _parent.OutputTypes[iinfo].ItemType.RawType);
                var srcTensorGetters = GetTensorValueGetters(input, _inputColIndices, _isInputVector, _parent.TFInputTypes, _fullySpecifiedShapes);
                return Utils.MarshalInvoke(MakeGetter<int>, type, input, iinfo, srcTensorGetters, activeOutputColNames, outputCache);
            }

            private Delegate MakeGetter<T>(IRow input, int iinfo, ITensorValueGetter[] srcTensorGetters, string[] activeOutputColNames, OutputCache outputCache)
            {
                Host.AssertValue(input);
                ValueGetter<VBuffer<T>> valuegetter = (ref VBuffer<T> dst) =>
                {
                    UpdateCacheIfNeeded(input.Position, srcTensorGetters, activeOutputColNames, outputCache);

                    var tensor = outputCache.Outputs[_parent.Outputs[iinfo]];
                    var tensorSize = tensor.Shape.Where(x => x > 0).Aggregate((x, y) => x * y);

                    var editor = VBufferEditor.Create(ref dst, (int)tensorSize);
                    TensorFlowUtils.FetchData<T>(tensor.Data, editor.Values);
                    dst = editor.Commit();
                };
                return valuegetter;
            }

            private void UpdateCacheIfNeeded(long position, ITensorValueGetter[] srcTensorGetters, string[] activeOutputColNames, OutputCache outputCache)
            {
                if (outputCache.Position != position)
                {
                    var runner = _parent.Session.GetRunner();
                    for (int i = 0; i < _inputColIndices.Length; i++)
                    {
                        var inputName = _parent.Inputs[i];
                        runner.AddInput(inputName, srcTensorGetters[i].GetTensor());
                    }

                    var tensors = runner.Fetch(activeOutputColNames).Run();
                    Contracts.Assert(tensors.Length > 0);

                    for (int j = 0; j < tensors.Length; j++)
                        outputCache.Outputs[activeOutputColNames[j]] = tensors[j];

                    outputCache.Position = position;
                }
            }

            public override Func<int, bool> GetDependencies(Func<int, bool> activeOutput)
            {
                return col => Enumerable.Range(0, _parent.Outputs.Length).Any(i => activeOutput(i)) && _inputColIndices.Any(i => i == col);
            }

            protected override Schema.Column[] GetOutputColumnsCore()
            {
                var info = new Schema.Column[_parent.Outputs.Length];
                for (int i = 0; i < _parent.Outputs.Length; i++)
                    info[i] = new Schema.Column(_parent.Outputs[i], _parent.OutputTypes[i], null);
                return info;
            }
        }

        [TlcModule.EntryPoint(Name = "Transforms.TensorFlowScorer",
            Desc = Summary,
            UserName = UserName,
            ShortName = ShortName,
            XmlInclude = new[] { @"<include file='../Microsoft.ML.TensorFlow/doc.xml' path='doc/members/member[@name=""TensorflowTransform""]/*' />" })]
        public static CommonOutputs.TransformOutput TensorFlowScorer(IHostEnvironment env, Arguments input)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(input, nameof(input));

            var h = EntryPointUtils.CheckArgsAndCreateHost(env, "TensorFlow", input);
            var view = Create(h, input, input.Data);
            return new CommonOutputs.TransformOutput()
            {
                Model = new TransformModel(h, view, input.Data),
                OutputData = view
            };
        }

        private interface ITensorValueGetter
        {
            TFTensor GetTensor();

            void BufferTrainingData();

            TFTensor GetBufferedBatchTensor();
        }

        private class TensorValueGetter<T> : ITensorValueGetter
        {
            private readonly ValueGetter<T> _srcgetter;
            private readonly T[] _bufferedData;
            private readonly TFShape _tfShape;
            private int _position;

            public TensorValueGetter(IRow input, int colIndex, TFShape tfShape)
            {
                _srcgetter = input.GetGetter<T>(colIndex);
                _tfShape = tfShape;
                long size = 0;
                _position = 0;
                if (tfShape.dims.Length != 0)
                {
                    size = 1;
                    foreach (var dim in tfShape.dims)
                        size *= dim;
                }
                _bufferedData = new T[size];
            }

            public TFTensor GetTensor()
            {
                var scalar = default(T);
                _srcgetter(ref scalar);
                return TFTensor.CreateScalar(scalar);
            }

            public void BufferTrainingData()
            {
                var scalar = default(T);
                _srcgetter(ref scalar);
                _bufferedData[_position++] = scalar;
            }

            public TFTensor GetBufferedBatchTensor()
            {
                var tensor = TFTensor.Create(_bufferedData, _bufferedData.Length, _tfShape);
                _position = 0;
                return tensor;
            }
        }

        private class TensorValueGetterVec<T> : ITensorValueGetter
        {
            private readonly ValueGetter<VBuffer<T>> _srcgetter;
            private readonly TFShape _tfShape;
            private VBuffer<T> _vBuffer;
            private T[] _denseData;
            private readonly T[] _bufferedData;
            private int _position;

            public TensorValueGetterVec(IRow input, int colIndex, TFShape tfShape)
            {
                _srcgetter = input.GetGetter<VBuffer<T>>(colIndex);
                _tfShape = tfShape;
                _vBuffer = default;
                _denseData = default;

                long size = 0;
                _position = 0;
                if (tfShape.dims.Length != 0)
                {
                    size = 1;
                    foreach (var dim in tfShape.dims)
                        size *= dim;
                }
                _bufferedData = new T[size];
            }

            public TFTensor GetTensor()
            {
                _srcgetter(ref _vBuffer);

                Utils.EnsureSize(ref _denseData, _vBuffer.Length, keepOld: false);
                _vBuffer.CopyTo(_denseData);

                return TFTensor.Create(_denseData, _vBuffer.Length, _tfShape);
            }

            public void BufferTrainingData()
            {
                _srcgetter(ref _vBuffer);
                _vBuffer.CopyTo(_bufferedData, _position);
                _position += _vBuffer.Length;
            }

            public TFTensor GetBufferedBatchTensor()
            {
                var tensor = TFTensor.Create(_bufferedData, _bufferedData.Length, _tfShape);
                _position = 0;
                return tensor;
            }
        }
    }

    public sealed class TensorFlowEstimator : IEstimator<TensorFlowTransform>
    {
        private readonly IHost _host;
        private readonly TensorFlowTransform.Arguments _args;
        private readonly TensorFlowModelInfo _tensorFlowModel;
        private readonly TFDataType[] _tfInputTypes;
        private readonly ColumnType[] _outputTypes;
        private TensorFlowTransform _transformer;

        public TensorFlowEstimator(IHostEnvironment env, string modelLocation, string[] inputs, string[] outputs)
            : this(env, TensorFlowUtils.LoadTensorFlowModel(env, modelLocation), inputs, outputs)
        {
        }

        public TensorFlowEstimator(IHostEnvironment env, TensorFlowModelInfo tensorFlowModel, string[] inputs, string[] outputs)
            : this(env, CreateArguments(tensorFlowModel, inputs, outputs), tensorFlowModel)
        {
        }

        public TensorFlowEstimator(IHostEnvironment env, TensorFlowTransform.Arguments args)
            : this(env, args, TensorFlowUtils.LoadTensorFlowModel(env, args.ModelLocation))
        {
        }

        public TensorFlowEstimator(IHostEnvironment env, TensorFlowTransform.Arguments args, TensorFlowModelInfo tensorFlowModel)
        {
            _host = Contracts.CheckRef(env, nameof(env)).Register(nameof(TensorFlowEstimator));
            _args = args;
            _tensorFlowModel = tensorFlowModel;
            var inputTuple = TensorFlowTransform.GetInputInfo(_host, tensorFlowModel.Session, args.InputColumns);
            _tfInputTypes = inputTuple.tfInputTypes;
            var outputTuple = TensorFlowTransform.GetOutputInfo(_host, tensorFlowModel.Session, args.OutputColumns);
            _outputTypes = outputTuple.outputTypes;
        }

        private static TensorFlowTransform.Arguments CreateArguments(TensorFlowModelInfo tensorFlowModel, string[] inputs, string[] outputs)
        {
            var args = new TensorFlowTransform.Arguments();
            args.ModelLocation = tensorFlowModel.ModelPath;
            args.InputColumns = inputs;
            args.OutputColumns = outputs;
            args.ReTrain = false;
            return args;
        }
        public SchemaShape GetOutputSchema(SchemaShape inputSchema)
        {
            _host.CheckValue(inputSchema, nameof(inputSchema));
            var result = inputSchema.Columns.ToDictionary(x => x.Name);
            var resultDic = inputSchema.Columns.ToDictionary(x => x.Name);
            for (var i = 0; i < _args.InputColumns.Length; i++)
            {
                var input = _args.InputColumns[i];
                if (!inputSchema.TryFindColumn(input, out var col))
                    throw _host.ExceptSchemaMismatch(nameof(inputSchema), "input", input);
                if (!(col.Kind == SchemaShape.Column.VectorKind.Vector))
                    throw _host.ExceptSchemaMismatch(nameof(inputSchema), "input", input, nameof(VectorType), col.GetTypeString());
                var expectedType = TensorFlowUtils.Tf2MlNetType(_tfInputTypes[i]);
                if (col.ItemType != expectedType)
                    throw _host.ExceptSchemaMismatch(nameof(inputSchema), "input", input, expectedType.ToString(), col.ItemType.ToString());
            }
            for (var i = 0; i < _args.OutputColumns.Length; i++)
            {
                resultDic[_args.OutputColumns[i]] = new SchemaShape.Column(_args.OutputColumns[i],
                    _outputTypes[i].IsKnownSizeVector ? SchemaShape.Column.VectorKind.Vector
                    : SchemaShape.Column.VectorKind.VariableVector, _outputTypes[i].ItemType, false);
            }
            return new SchemaShape(resultDic.Values);
        }

        public TensorFlowTransform Fit(IDataView input)
        {
            _host.CheckValue(input, nameof(input));
            if (_transformer == null)
            {
                _transformer = _args.ReTrain ? new TensorFlowTransform(_host, _args, _tensorFlowModel, input) :
                    new TensorFlowTransform(_host, _tensorFlowModel.Session, _args.InputColumns, _args.OutputColumns,
                    TensorFlowUtils.IsSavedModel(_host, _args.ModelLocation) ? _args.ModelLocation : null, false);
            }
            // Validate input schema.
            _transformer.GetOutputSchema(input.Schema);
            return _transformer;
        }
    }

    public static class TensorFlowStaticExtensions
    {
        private sealed class OutColumn : Vector<float>
        {
            public PipelineColumn Input { get; }

            public OutColumn(Vector<float> input, string modelFile)
                : base(new Reconciler(modelFile), input)
            {
                Input = input;
            }

            public OutColumn(Vector<float> input, TensorFlowModelInfo tensorFlowModel)
                : base(new Reconciler(tensorFlowModel), input)
            {
                Input = input;
            }
        }

        private sealed class Reconciler : EstimatorReconciler
        {
            private readonly string _modelFile;
            private readonly TensorFlowModelInfo _tensorFlowModel;

            public Reconciler(string modelFile)
            {
                Contracts.AssertNonEmpty(modelFile);
                _modelFile = modelFile;
                _tensorFlowModel = null;
            }

            public Reconciler(TensorFlowModelInfo tensorFlowModel)
            {
                Contracts.CheckValue(tensorFlowModel, nameof(tensorFlowModel));

                _modelFile = null;
                _tensorFlowModel = tensorFlowModel;
            }

            public override IEstimator<ITransformer> Reconcile(IHostEnvironment env,
                PipelineColumn[] toOutput,
                IReadOnlyDictionary<PipelineColumn, string> inputNames,
                IReadOnlyDictionary<PipelineColumn, string> outputNames,
                IReadOnlyCollection<string> usedNames)
            {
                Contracts.Assert(toOutput.Length == 1);

                var outCol = (OutColumn)toOutput[0];
                if (_modelFile == null)
                {
                    return new TensorFlowEstimator(env, _tensorFlowModel, new[] { inputNames[outCol.Input] }, new[] { outputNames[outCol] });
                }
                else
                {
                    return new TensorFlowEstimator(env, _modelFile, new[] { inputNames[outCol.Input] }, new[] { outputNames[outCol] });
                }
            }
        }

        // REVIEW: this method only covers one use case of using TensorFlow models: consuming one
        // input and producing one output of floats.
        // We could consider selectively adding some more extensions to enable common scenarios.
        /// <summary>
        /// Load the TensorFlow model from <paramref name="modelFile"/> and run it on the input column and extract one output column.
        /// The inputs and outputs are matched to TensorFlow graph nodes by name.
        /// </summary>
        public static Vector<float> ApplyTensorFlowGraph(this Vector<float> input, string modelFile)
        {
            Contracts.CheckValue(input, nameof(input));
            Contracts.CheckNonEmpty(modelFile, nameof(modelFile));
            return new OutColumn(input, modelFile);
        }

        /// <summary>
        /// Run a TensorFlow model provided through <paramref name="tensorFlowModel"/> on the input column and extract one output column.
        /// The inputs and outputs are matched to TensorFlow graph nodes by name.
        /// </summary>
        public static Vector<float> ApplyTensorFlowGraph(this Vector<float> input, TensorFlowModelInfo tensorFlowModel)
        {
            Contracts.CheckValue(input, nameof(input));
            Contracts.CheckValue(tensorFlowModel, nameof(tensorFlowModel));
            return new OutColumn(input, tensorFlowModel);
        }
    }
}
