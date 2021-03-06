﻿using TwitterRWR.Data;
using TweetRecommender.Others;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading; // Multi-threading Library
using System.Threading.Tasks;
using System.Diagnostics;

namespace TweetRecommender 
{
    public enum Methodology // Total: 16
    { 
        BASELINE,
        INCL_FRIENDSHIP, INCL_FOLLOWSHIP_ON_THIRDPARTY, INCL_AUTHORSHIP, INCL_MENTIONCOUNT,
        INCL_ALLFOLLOWSHIP, INCL_FRIENDSHIP_AUTHORSHIP, INCL_FRIENDSHIP_MENTIONCOUNT,
        INCL_FOLLOWSHIP_ON_THIRDPARTY_AND_AUTHORSHIP, INCL_FOLLOWSHIP_ON_THIRDPARTY_AND_MENTIONCOUNT,
        INCL_AUTHORSHIP_AND_MENTIONCOUNT,
        EXCL_FRIENDSHIP, EXCL_FOLLOWSHIP_ON_THIRDPARTY, EXCL_AUTHORSHIP, EXCL_MENTIONCOUNT,
        ALL
    }
    public enum Feature { FRIENDSHIP, FOLLOWSHIP_ON_THIRDPARTY, AUTHORSHIP, MENTIONCOUNT } 
    public enum EvaluationMetric { MAP, RECALL, LIKE, HIT }

    public struct kFoldThreadParameter
    {
        public DataLoader loader;
        public Methodology methodology;
        public int fold; // kth fold
        public int nIteration;

        public kFoldThreadParameter(DataLoader loader, Methodology methodology, int fold, int nIteration) 
        {
            this.loader = loader;
            this.methodology = methodology;
            this.fold = fold;
            this.nIteration = nIteration;
        }
    }

    public class Experiment 
    {
        // Properties
        private string dbPath;
        private Dictionary<EvaluationMetric, double> finalResult;
        private List<EvaluationMetric> metrics;
        private int numOfFriend;

        // K-fold Multi-threading
        private Semaphore kFoldSemaphore;
        private Object kFoldLocker = new Object();

        // Constructors
        public Experiment(string dbPath)
        {
            this.dbPath = dbPath;
        }

        // Primary Methods
        public void startPersonalizedPageRank(object parameterObject) 
        {
            Stopwatch pageRankStopwatch = Stopwatch.StartNew();
            // PageRank Environment Setting
            personalizedPageRankThreadParamters parameters = (personalizedPageRankThreadParamters)parameterObject;
            int nFold = parameters.kFold;
            int nIteration = parameters.nIteration;
            Methodology methodology = parameters.methodology;

            try
            {
                // Get ego user's ID and his like count
                long egoID = long.Parse(Path.GetFileNameWithoutExtension(this.dbPath));
                
                // Final result to put the experimental result per fold together
                this.finalResult = new Dictionary<EvaluationMetric, double>(); // <'HIT(0)', double> or <'AVGPRECISION(1)', double>
                foreach (EvaluationMetric metric in Enum.GetValues(typeof(EvaluationMetric)))
                    this.finalResult.Add(metric, 0d); // Initialization

                // Need to avoid the following error: "Collection was modified; enumeration operation may not execute"
                metrics = new List<EvaluationMetric>(this.finalResult.Keys);

                // K-Fold Cross Validation
                kFoldSemaphore = new Semaphore(nFold, nFold);
                List<Thread> kFoldThreadList = new List<Thread>(); // 'Thread': Standard Library Class
                for (int fold = 0; fold < 1; fold++) // One fold to One thread
                {
                    // Load graph information from database and then configurate the graph
                    DataLoader loader = new DataLoader(this.dbPath);
                    loader.setEgoNetwork();
                    loader.setEgoTimeline();
                    loader.splitTimelineToKfolds(nFold);
                    // |Friend|
                    this.numOfFriend = loader.getNumOfFriend();
                    // Multi-Threading
                    kFoldSemaphore.WaitOne(); // Wait until Semaphore released
                    Thread thread = new Thread(new ParameterizedThreadStart(runKfoldCrossValidation));
                    kFoldThreadParameter kFoldparameters = new kFoldThreadParameter(loader, methodology, fold, nIteration); // 'ThreadParams': defined in 'Experiment.cs'
                    thread.Start(kFoldparameters);
                    kFoldThreadList.Add(thread);
                }
                // Synchronization: Wait until threads be terminated
                foreach (Thread thread in kFoldThreadList)
                    thread.Join();

                if (Program.isValidTrainSet == false)
                    return;
                // Result: <Ego ID>\t<Experi Code>\t<N-fold>\t<iteration>\t<MAP>\t<Recall>\t<|LIKE|>\t<|HIT|>\t<|Friend|>
                pageRankStopwatch.Stop();
                lock(Program.outFileLocker)
                {

                    Program.logger.Write(egoID + "\t" + (int)methodology + "\t" + nFold + "\t" + nIteration);
                    foreach (EvaluationMetric metric in metrics)
                    {
                        switch (metric)
                        {
                            case EvaluationMetric.MAP:
                                Program.logger.Write("\t{0:F15}", (finalResult[metric] / 1));
                                break;
                            case EvaluationMetric.RECALL:
                                Program.logger.Write("\t{0:F15}", (finalResult[metric] / 1));
                                break;
                            case EvaluationMetric.LIKE:
                                Program.logger.Write("\t" + (finalResult[metric]));
                                break;
                            case EvaluationMetric.HIT:
                                Program.logger.Write("\t" + (finalResult[metric]));
                                break;
                        }
                    }
                    // |Friend|
                    Program.logger.Write("\t" + this.numOfFriend);
                    // Output Execution Time
                    Program.logger.WriteLine("\t" + Tools.getExecutionTime(pageRankStopwatch));
                    Program.logger.Flush();
                    // Console Output
                    Console.WriteLine("|Friend|: " + this.numOfFriend);
                    Console.WriteLine("Excution Time: " + Tools.getExecutionTime(pageRankStopwatch));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
            finally
            {
                Program.dbSemaphore.Release();
            }
        }

        private void runKfoldCrossValidation(object parameters)
        {
            try
            {
                // Setting environment for experiments
                kFoldThreadParameter p = (kFoldThreadParameter)parameters;
                DataLoader loader = p.loader;
                Methodology methodology = p.methodology;
                int fold = p.fold;
                int nIteration = p.nIteration;

                // #1 Core Part: 'EgoNetwork DB' --> 'Graph Strctures(Node, Edge)'
                loader.setTrainTestSet(fold);
                if (Program.isValidTrainSet == false)
                    return;
                List<Feature> features = loader.getFeaturesOnMethodology(methodology);
                loader.setGraphConfiguration(features);

                // Graph Elements: Nodes and edges
                Dictionary<int, Node> nodes = loader.allNodes;
                Dictionary<int, List<ForwardLink>> edges = loader.allLinksFromNodes;

                // Incase: Mention Count(O), Friendship(X)
                if (methodology == Methodology.INCL_MENTIONCOUNT
                    || methodology == Methodology.INCL_AUTHORSHIP_AND_MENTIONCOUNT
                    || methodology == Methodology.INCL_FOLLOWSHIP_ON_THIRDPARTY_AND_MENTIONCOUNT
                    || methodology == Methodology.EXCL_FRIENDSHIP)
                {
                    foreach (List<ForwardLink> forwardLinks in edges.Values)
                    {
                        for (int i = 0; i < forwardLinks.Count; i++)
                        {
                            if (forwardLinks[i].type == EdgeType.FRIENDSHIP)
                            {
                                ForwardLink revisedForwardLink = forwardLinks[i];
                                revisedForwardLink.type = EdgeType.UNDEFINED; // FRIENDSHIP --> UNDEFINED
                                forwardLinks[i] = revisedForwardLink;
                            }
                        }
                    }
                }

                // #2 Core Part: 'Graph Strcture(Nodes, Edges)' --> 'PageRank Matrix(2D-ragged array)'
                Graph graph = new Graph(nodes, edges);
                graph.buildGraph();

                // #3 Core Part: Recommendation list(Personalized PageRanking Algorithm)
                Recommender recommender = new Recommender(graph);
                var recommendation = recommender.Recommendation(0, 0.15f, nIteration); // '0': Ego Node's Index, '0.15f': Damping Factor

                // #4 Core Part: Validation - AP(Average Precision)
                DataSet testSet = loader.getTestSet();
                HashSet<long> egoLikedTweets = testSet.getEgoLikedTweets();
                int hit = 0, like = 0;
                double AP = 0.0, recall = 0.0; // Average Precision
                for (int i = 0; i < recommendation.Count; i++)
                {
                    if (egoLikedTweets.Contains(((Tweet)recommendation[i]).ID))
                    {
                        hit += 1;
                        AP += (double)hit / (i + 1);
                    }
                }
                // LIKE
                like = (int)egoLikedTweets.Count;
                // Average Precision & Recall
                if (hit != 0)
                {
                    AP /= hit;
                    recall = (double)hit / like;
                }
                else
                {
                    AP = 0.0;
                    recall = 0.0;
                }

                // Add current result to final one
                lock (kFoldLocker)
                {
                    foreach (EvaluationMetric metric in this.metrics)
                    {
                        switch (metric)
                        {
                            case EvaluationMetric.MAP:
                                this.finalResult[metric] += AP;
                                break;
                            case EvaluationMetric.RECALL:
                                this.finalResult[metric] += recall;
                                break;
                            case EvaluationMetric.LIKE:
                                this.finalResult[metric] += like;
                                break;
                            case EvaluationMetric.HIT:
                                this.finalResult[metric] += hit;
                                break;
                        }
                    }
                }
            }
            catch (FileNotFoundException e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                kFoldSemaphore.Release();
            }
        }
    }
}
