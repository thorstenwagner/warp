﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using Warp.Tools;
using TorchSharp.Tensor;
using TorchSharp.NN;
using static TorchSharp.NN.Modules;
using static TorchSharp.NN.Functions;
using static TorchSharp.NN.Losses;
using static TorchSharp.ScalarExtensionMethods;
using TorchSharp;
using System.Diagnostics;

namespace Warp
{
    public class BoxNetTorch
    {
        public const float PixelSize = 8;

        public static readonly int2 DefaultDimensionsTrain = new int2(512);
        public static readonly int2 DefaultDimensionsValidTrain = new int2(384);
        public static readonly int2 DefaultDimensionsPredict = new int2(512);
        public static readonly int2 DefaultDimensionsValidPredict = new int2(384);
        public static readonly int DefaultBatchTrain = 8;
        public static readonly int DefaultBatchPredict = 1;

        public readonly int2 BoxDimensions;
        public readonly int BatchSize = 32;
        public readonly int DeviceBatch = 32;
        public readonly int[] Devices;
        public readonly int NDevices;

        private UNet2D[] UNetModel;

        private TorchTensor[] TensorSource;
        private TorchTensor[] TensorTarget;
        private TorchTensor[] TensorClassWeights;

        private Loss[] Loss;
        private Optimizer Optimizer;

        private Image ResultPredictedArgmax;
        private Image ResultPredictedSoftmax;
        private float[] h_ResultPredictedArgmax;
        private float[] h_ResultPredictedSoftmax;
        private float[] ResultLoss = new float[1];

        private bool IsDisposed = false;

        public BoxNetTorch(int2 boxDimensions, float[] classWeights, int[] devices, int batchSize = 8)
        {
            Devices = devices;
            NDevices = Devices.Length;

            BatchSize = Math.Max(batchSize, NDevices);
            DeviceBatch = BatchSize / NDevices;
            if (BatchSize % NDevices != 0)
                throw new Exception("Batch size must be divisible by the number of devices.");

            BoxDimensions = boxDimensions;

            UNetModel = new UNet2D[NDevices];
            TensorSource = new TorchTensor[NDevices];
            TensorTarget = new TorchTensor[NDevices];
            TensorClassWeights = new TorchTensor[NDevices];

            Loss = new Loss[NDevices];
            if (classWeights.Length != 3)
                throw new Exception();

            //Helper.ForCPU(0, NDevices, NDevices, null, (i, threadID) =>
            for (int i = 0; i < NDevices; i++)
            {
                int DeviceID = Devices[i];

                UNetModel[i] = UNet2D(3, 1, 1, 3, 1, false, false);
                UNetModel[i].ToCuda(DeviceID);

                TensorSource[i] = Float32Tensor.Zeros(new long[] { DeviceBatch, 1, BoxDimensions.Y, BoxDimensions.X }, DeviceType.CUDA, DeviceID);
                TensorTarget[i] = Float32Tensor.Zeros(new long[] { DeviceBatch, 3, BoxDimensions.Y, BoxDimensions.X }, DeviceType.CUDA, DeviceID);

                TensorClassWeights[i] = Float32Tensor.Zeros(new long[] { 3 }, DeviceType.CUDA, DeviceID);
                GPU.CopyHostToDevice(classWeights, TensorClassWeights[i].DataPtr(), 3);

                Loss[i] = CE(TensorClassWeights[i]);

            }//, null);
            Optimizer = Optimizer.SGD(UNetModel[0].GetParameters(), 0.01, 0.9, false, 5e-4);

            ResultPredictedArgmax = new Image(IntPtr.Zero, new int3(BoxDimensions.X, BoxDimensions.Y, BatchSize));
            ResultPredictedSoftmax = new Image(IntPtr.Zero, new int3(BoxDimensions.X, BoxDimensions.Y, BatchSize * 3));

            h_ResultPredictedArgmax = new float[ResultPredictedArgmax.ElementsReal];
            h_ResultPredictedSoftmax = new float[ResultPredictedSoftmax.ElementsReal];
        }

        private void ScatterData(Image src, TorchTensor[] dest)
        {
            src.GetDevice(Intent.Read);

            for (int i = 0; i < NDevices; i++)
                GPU.CopyDeviceToDevice(src.GetDeviceSlice(i * DeviceBatch, Intent.Read),
                                       dest[i].DataPtr(),
                                       DeviceBatch * BoxDimensions.Elements());
        }

        private void SyncParams()
        {
            for (int i = 1; i < NDevices; i++)
                UNetModel[0].SynchronizeTo(UNetModel[i], Devices[i]);
        }

        private void GatherGrads()
        {
            for (int i = 1; i < NDevices; i++)
                UNetModel[0].GatherGrad(UNetModel[i]);
        }


        public void Predict(Image data, out Image predictionArgmax, out Image predictionSoftmax)
        {
            ScatterData(data, TensorSource);
            ResultPredictedArgmax.GetDevice(Intent.Write);

            Helper.ForCPU(0, NDevices, NDevices, null, (i, threadID) =>
            {
                using (var mode = new InferenceMode(true))
                {
                    UNetModel[i].Eval();

                    using (TorchTensor t_Prediction = UNetModel[i].Forward(TensorSource[i]))
                    using (TorchTensor t_PredictionSoftMax = t_Prediction.Softmax(1))
                    using (TorchTensor t_PredictionArgMax = t_PredictionSoftMax.Argmax(1, false))
                    using (TorchTensor t_PredictionArgMaxFP = t_PredictionArgMax.ToType(ScalarType.Float32))
                    {
                        GPU.CopyDeviceToDevice(t_PredictionSoftMax.DataPtr(),
                                               ResultPredictedSoftmax.GetDeviceSlice(i * DeviceBatch * 3, Intent.Write),
                                               DeviceBatch * BoxDimensions.Elements() * 3);

                        GPU.CopyDeviceToDevice(t_PredictionArgMaxFP.DataPtr(),
                                               ResultPredictedArgmax.GetDeviceSlice(i * DeviceBatch, Intent.Write),
                                               DeviceBatch * BoxDimensions.Elements());
                    }
                }
            }, null);

            predictionArgmax = ResultPredictedArgmax;
            predictionSoftmax = ResultPredictedSoftmax;
        }

        public void Predict(Image data, out float[] h_predictionArgmax, out float[] h_predictionSoftmax)
        {
            Predict(data, out ResultPredictedArgmax, out ResultPredictedSoftmax);

            GPU.CopyDeviceToHost(ResultPredictedSoftmax.GetDevice(Intent.Read),
                                 h_ResultPredictedSoftmax,
                                 BatchSize * BoxDimensions.Elements() * 3);

            GPU.CopyDeviceToHost(ResultPredictedArgmax.GetDevice(Intent.Read),
                                 h_ResultPredictedArgmax,
                                 BatchSize * BoxDimensions.Elements());

            h_predictionArgmax = h_ResultPredictedArgmax;
            h_predictionSoftmax = h_ResultPredictedSoftmax;
        }

        public void Train(Image source,
                          Image target,
                          float learningRate,
                          bool needOutput,
                          out Image prediction,
                          out float[] loss)
        {
            GPU.CheckGPUExceptions();

            Optimizer.SetLearningRateSGD(learningRate);
            Optimizer.ZeroGrad();

            SyncParams();
            ResultPredictedArgmax.GetDevice(Intent.Write);

            Helper.ForCPU(0, NDevices, NDevices, null, (i, threadID) =>
            {
                UNetModel[i].Train();
                UNetModel[i].ZeroGrad();

                GPU.CopyDeviceToDevice(source.GetDeviceSlice(i * DeviceBatch, Intent.Read),
                                       TensorSource[i].DataPtr(),
                                       DeviceBatch * (int)BoxDimensions.Elements());
                GPU.CopyDeviceToDevice(target.GetDeviceSlice(i * DeviceBatch * 3, Intent.Read),
                                       TensorTarget[i].DataPtr(),
                                       DeviceBatch * (int)BoxDimensions.Elements() * 3);

                GPU.CheckGPUExceptions();

                using (TorchTensor TargetArgMax = TensorTarget[i].Argmax(1))
                using (TorchTensor Prediction = UNetModel[i].Forward(TensorSource[i]))
                using (TorchTensor PredictionLoss = Loss[i](Prediction, TargetArgMax))
                {
                    if (needOutput)
                    {
                        using (TorchTensor PredictionArgMax = Prediction.Argmax(1))
                        using (TorchTensor PredictionArgMaxFP = PredictionArgMax.ToType(ScalarType.Float32))
                        {
                            GPU.CopyDeviceToDevice(PredictionArgMaxFP.DataPtr(),
                                                   ResultPredictedArgmax.GetDeviceSlice(i * DeviceBatch, Intent.Write),
                                                   DeviceBatch * (int)BoxDimensions.Elements());
                        }
                    }

                    if (i == 0)
                        GPU.CopyDeviceToHost(PredictionLoss.DataPtr(), ResultLoss, 1);
                    //Debug.WriteLine(i + ": " + ResultLoss[0]);

                    PredictionLoss.Backward();
                }
            }, null);

            GatherGrads();

            //if (NDevices > 1)
            //    UNetModel[0].ScaleGrad(1f / NDevices);

            Optimizer.Step();

            prediction = ResultPredictedArgmax;
            loss = ResultLoss;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Helper.PathToFolder(path));

            UNetModel[0].Save(path);
        }

        public void Load(string path)
        {
            for (int i = 0; i < NDevices; i++)
            {
                UNetModel[i].Load(path, DeviceType.CUDA, Devices[i]);
                //UNetModel[i].ToCuda(Devices[i]);
            }
        }

        ~BoxNetTorch()
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (this)
            {
                if (!IsDisposed)
                {
                    IsDisposed = true;

                    ResultPredictedArgmax.Dispose();

                    for (int i = 0; i < NDevices; i++)
                    {
                        TensorSource[i].Dispose();
                        TensorTarget[i].Dispose();
                        TensorClassWeights[i].Dispose();

                        UNetModel[i].Dispose();
                    }

                    Optimizer.Dispose();
                }
            }
        }
    }
}
