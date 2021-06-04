﻿using AdvUtils;
using Newtonsoft.Json;
using Seq2SeqSharp;
using Seq2SeqSharp.Applications;
using Seq2SeqSharp.Corpus;
using Seq2SeqSharp.Metrics;
using Seq2SeqSharp.Optimizer;
using Seq2SeqSharp.Tools;
using Seq2SeqSharp.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SeqClassificationConsole
{
    class Program
    {
        private static SeqClassificationOptions opts = new SeqClassificationOptions();
        private static void ss_EvaluationWatcher(object sender, EventArgs e)
        {
            EvaluationEventArg ep = e as EvaluationEventArg;
            Logger.WriteLine(Logger.Level.info, ep.Color, ep.Message);

            if (String.IsNullOrEmpty(opts.NotifyEmail) == false)
            {
                Email.Send(ep.Title, ep.Message, opts.NotifyEmail, new string[] { opts.NotifyEmail });
            }
        }

        private static void ss_StatusUpdateWatcher(object sender, EventArgs e)
        {
            CostEventArg ep = e as CostEventArg;

            TimeSpan ts = DateTime.Now - ep.StartDateTime;
            double sentPerMin = 0;
            double wordPerSec = 0;
            if (ts.TotalMinutes > 0)
            {
                sentPerMin = ep.ProcessedSentencesInTotal / ts.TotalMinutes;
            }

            if (ts.TotalSeconds > 0)
            {
                wordPerSec = ep.ProcessedWordsInTotal / ts.TotalSeconds;
            }

            Logger.WriteLine($"Update = {ep.Update}, Epoch = {ep.Epoch}, LR = {ep.LearningRate.ToString("F6")}, AvgCost = {ep.AvgCostInTotal.ToString("F4")}, Sent = {ep.ProcessedSentencesInTotal}, SentPerMin = {sentPerMin.ToString("F")}, WordPerSec = {wordPerSec.ToString("F")}");
        }

        public static string GetTimeStamp(DateTime timeStamp)
        {
            return string.Format("{0:yyyy}_{0:MM}_{0:dd}_{0:HH}h_{0:mm}m_{0:ss}s", timeStamp);
        }

        private static void ShowOptions(string[] args)
        {
            string commandLine = string.Join(" ", args);
            Logger.WriteLine($"SeqClassificationConsole v2.3.0 written by Zhongkai Fu(fuzhongkai@gmail.com)");
            Logger.WriteLine($"Command Line = '{commandLine}'");
        }

        static void Main(string[] args)
        {
            try
            {
                Logger.LogFile = $"{nameof(SeqClassificationConsole)}_{GetTimeStamp(DateTime.Now)}.log";
                ShowOptions(args);

                //Parse command line
                //   Seq2SeqOptions opts = new Seq2SeqOptions();
                ArgParser argParser = new ArgParser(args, opts);

                if (string.IsNullOrEmpty(opts.ConfigFilePath) == false)
                {
                    Logger.WriteLine($"Loading config file from '{opts.ConfigFilePath}'");
                    opts = JsonConvert.DeserializeObject<SeqClassificationOptions>(File.ReadAllText(opts.ConfigFilePath));
                }

                string strOpts = JsonConvert.SerializeObject(opts);
                Logger.WriteLine($"Configs: {strOpts}");

                SeqClassification ss = null;
                ModeEnums mode = (ModeEnums)Enum.Parse(typeof(ModeEnums), opts.Task);
                ShuffleEnums shuffleType = (ShuffleEnums)Enum.Parse(typeof(ShuffleEnums), opts.ShuffleType);

                if (mode == ModeEnums.Train)
                {
                    // Load train corpus
                    SequenceClassificationCorpus trainCorpus = new SequenceClassificationCorpus(corpusFilePath: opts.TrainCorpusPath, batchSize: opts.BatchSize, shuffleBlockSize: opts.ShuffleBlockSize,
                        maxSentLength: opts.MaxTrainSentLength, shuffleEnums: shuffleType);
                    // Load valid corpus
                    SequenceClassificationCorpus validCorpus = string.IsNullOrEmpty(opts.ValidCorpusPath) ? null : new SequenceClassificationCorpus(opts.ValidCorpusPath, opts.ValBatchSize, opts.ShuffleBlockSize, opts.MaxTestSentLength, shuffleEnums: shuffleType);

                    // Create learning rate
                    ILearningRate learningRate = new DecayLearningRate(opts.StartLearningRate, opts.WarmUpSteps, opts.WeightsUpdateCount);

                    // Create metrics
                    List<IMetric> metrics = new List<IMetric>();

                    // Create optimizer
                    IOptimizer optimizer = null;
                    if (String.Equals(opts.Optimizer, "Adam", StringComparison.InvariantCultureIgnoreCase))
                    {
                        optimizer = new AdamOptimizer(opts.GradClip, opts.Beta1, opts.Beta2);
                    }
                    else
                    {
                        optimizer = new RMSPropOptimizer(opts.GradClip, opts.Beta1);
                    }


                    if (!String.IsNullOrEmpty(opts.ModelFilePath) && File.Exists(opts.ModelFilePath))
                    {
                        //Incremental training
                        Logger.WriteLine($"Loading model from '{opts.ModelFilePath}'...");
                        ss = new SeqClassification(opts);

                        foreach (string word in ss.Vocab.TgtVocab)
                        {
                            metrics.Add(new SequenceLabelFscoreMetric(word));
                        }
                    }
                    else
                    {
                        // Load or build vocabulary
                        Vocab vocab = null;
                        if (!string.IsNullOrEmpty(opts.SrcVocab) && !string.IsNullOrEmpty(opts.TgtVocab))
                        {
                            Logger.WriteLine($"Loading source vocabulary from '{opts.SrcVocab}' and target vocabulary from '{opts.TgtVocab}'.");
                            // Vocabulary files are specified, so we load them
                            vocab = new Vocab(opts.SrcVocab, opts.TgtVocab);
                        }
                        else
                        {
                            Logger.WriteLine($"Building vocabulary from training corpus.");
                            // We don't specify vocabulary, so we build it from train corpus
                            vocab = new Vocab(trainCorpus, opts.VocabSize, sharedVocab: false);
                        }

                        foreach (string word in vocab.TgtVocab)
                        {
                            metrics.Add(new SequenceLabelFscoreMetric(word));
                        }

                        //New training
                        ss = new SeqClassification(opts, vocab);
                    }




                    // Add event handler for monitoring
                    ss.StatusUpdateWatcher += ss_StatusUpdateWatcher;
                    ss.EvaluationWatcher += ss_EvaluationWatcher;

                    // Kick off training
                    ss.Train(maxTrainingEpoch: opts.MaxEpochNum, trainCorpus: trainCorpus, validCorpus: validCorpus, learningRate: learningRate, optimizer: optimizer, metrics: metrics);
                }
                //else if (mode == ModeEnums.Valid)
                //{
                //    Logger.WriteLine($"Evaluate model '{opts.ModelFilePath}' by valid corpus '{opts.ValidCorpusPath}'");

                //    // Create metrics
                //    List<IMetric> metrics = new List<IMetric>
                //{
                //    new BleuMetric(),
                //    new LengthRatioMetric()
                //};

                //    // Load valid corpus
                //    ParallelCorpus validCorpus = new ParallelCorpus(opts.ValidCorpusPath, opts.SrcLang, opts.TgtLang, opts.ValBatchSize, opts.ShuffleBlockSize, opts.MaxSrcTestSentLength, opts.MaxTgtTestSentLength, shuffleEnums: shuffleType);

                //    ss = new Seq2Seq(opts);
                //    ss.EvaluationWatcher += ss_EvaluationWatcher;
                //    ss.Valid(validCorpus: validCorpus, metrics: metrics);
                //}
                else if (mode == ModeEnums.Test)
                {
                    Logger.WriteLine($"Test model: '{opts.ModelFilePath}'");
                    Logger.WriteLine($"Test set: '{opts.InputTestFile}'");
                    Logger.WriteLine($"Max test sentence length: '{opts.MaxTestSentLength}'");
                    Logger.WriteLine($"Beam search size: '{opts.BeamSearchSize}'");
                    Logger.WriteLine($"Batch size: '{opts.BatchSize}'");
                    Logger.WriteLine($"Shuffle type: '{opts.ShuffleType}'");
                    Logger.WriteLine($"Device ids: '{opts.DeviceIds}'");


                    if (File.Exists(opts.OutputFile))
                    {
                        Logger.WriteLine(Logger.Level.err, ConsoleColor.Yellow, $"Output file '{opts.OutputFile}' exist. Delete it.");
                        File.Delete(opts.OutputFile);
                    }

                    //Test trained model
                    ss = new SeqClassification(opts);
                    List<List<string>> inputBatchs = new List<List<string>>();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    foreach (string line in File.ReadLines(opts.InputTestFile))
                    {
                        List<string> tokens = line.Trim().Split(' ').ToList();
                        if (tokens.Count > opts.MaxTestSentLength - 2)
                        {
                            tokens = tokens.GetRange(0, opts.MaxTestSentLength - 2);
                        }
                        tokens.Insert(0, ParallelCorpus.CLS);
                        inputBatchs.Add(tokens);

                        if (inputBatchs.Count >= opts.BatchSize * ss.DeviceIds.Length)
                        {
                            var outputLines = RunBatchTest(opts, ss, inputBatchs);
                            File.AppendAllLines(opts.OutputFile, outputLines);
                            inputBatchs.Clear();
                        }
                    }

                    if (inputBatchs.Count > 0)
                    {
                        var outputLines = RunBatchTest(opts, ss, inputBatchs);
                        File.AppendAllLines(opts.OutputFile, outputLines);
                    }
                    stopwatch.Stop();

                    Logger.WriteLine($"Test mode execution time elapsed: '{stopwatch.Elapsed}'");
                }
                //else if (mode == ModeEnums.DumpVocab)
                //{
                //    ss = new Seq2Seq(opts);
                //    ss.DumpVocabToFiles(opts.SrcVocab, opts.TgtVocab);
                //}
                else
                {
                    argParser.Usage();
                }
            }
            catch (Exception err)
            {
                Logger.WriteLine($"Exception: '{err.Message}'");
                Logger.WriteLine($"Call stack: '{err.StackTrace}'");
            }
        }

        private static List<string> RunBatchTest(SeqClassificationOptions opts, SeqClassification ss, List<List<string>> inputBatchs)
        {
            List<string> outputLines = new List<string>();

            (var outputBeamTokensBatch, var alignmentBeamTokensBatch) = ss.Test(inputBatchs, 1, hypPrefix: ""); // shape [beam size, batch size, tgt token size]
            for (int batchIdx = 0; batchIdx < inputBatchs.Count; batchIdx++)
            {
                for (int beamIdx = 0; beamIdx < outputBeamTokensBatch.Count; beamIdx++)
                {
                    outputLines.Add(String.Join(" ", outputBeamTokensBatch[beamIdx][batchIdx]));
                }
            }

            return outputLines;
        }
    }
}