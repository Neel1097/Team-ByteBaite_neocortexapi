﻿// Copyright (c) Damir Dobric. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.
using NeoCortexApi.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using NeoCortexApi.Utility;
using static NeoCortexApi.Entities.Connections;
using System.Diagnostics;

namespace NeoCortexApi
{
    /// <summary>
    /// Implementation of Temporal Memory algorithm.
    /// </summary>
    public class TemporalMemory : IHtmAlgorithm<int[], ComputeCycle>//: IComputeDecorator
    {
        private static readonly double EPSILON = 0.00001;

        private static readonly int cIndexofACTIVE_COLUMNS = 0;

        private Connections connections;

        public string Name { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        /// <summary>
        /// Uses the specified <see cref="Connections"/> object to Build the structural  anatomy needed by this <see cref="TemporalMemory"/> 
        /// to implement its algorithms.<br/>
        /// 
        /// The connections object holds the <see cref="Column"/> and <see cref="Cell"/> infrastructure, and is used by both the <see cref="SpatialPooler"/>
        /// and <see cref="TemporalMemory"/>. Either of these can be used separately, and therefore this Connections object may have its <see cref="Column"/>s 
        /// and <see cref="Cell"/>s initialized by either the init method of the <see cref="SpatialPooler"/> or the init method of the <see cref="TemporalMemory"/>. 
        /// We check for this so that complete initialization of both <see cref="Column"/>s and <see cref="Cell"/>s occurs, without either being redundant
        /// (initialized more than once). However, <see cref="Cell"/>s only get created when initializing a <see cref="TemporalMemory"/>, because 
        /// they are not used by the <see cref="SpatialPooler"/>.
        /// </summary>
        /// <param name="conn"><see cref="Connections"/> object</param>
        public void Init(Connections conn)
        {
            this.connections = conn;

            SparseObjectMatrix<Column> matrix = this.connections.HtmConfig.Memory == null ?
                new SparseObjectMatrix<Column>(this.connections.HtmConfig.ColumnDimensions) :
                    (SparseObjectMatrix<Column>)this.connections.HtmConfig.Memory;

            this.connections.HtmConfig.Memory = matrix;

            int numColumns = matrix.getMaxIndex() + 1;
            this.connections.HtmConfig.NumColumns = numColumns;
            int cellsPerColumn = this.connections.HtmConfig.CellsPerColumn;
            Cell[] cells = new Cell[numColumns * cellsPerColumn];

            //Used as flag to determine if Column objects have been created.
            Column colZero = matrix.getObject(0);
            for (int i = 0; i < numColumns; i++)
            {
                Column column = colZero == null ? new Column(cellsPerColumn, i, this.connections.HtmConfig.SynPermConnected, this.connections.HtmConfig.NumInputs) : matrix.getObject(i);
                for (int j = 0; j < cellsPerColumn; j++)
                {
                    cells[i * cellsPerColumn + j] = column.Cells[j];
                }
                //If columns have not been previously configured
                if (colZero == null)
                    matrix.set(i, column);

            }
            //Only the TemporalMemory initializes cells so no need to test for redundancy
            this.connections.Cells = cells;
        }

        /// <summary>
        /// Performs the whole calculation of Temporal memory algorithm.
        /// Calculation takes two parts:
        /// <list type="number">
        /// <item>Calculation of the cells, which become active in the current cycle.</item>
        /// <item>Calculation of dendrite segments which becom active in the current cycle.</item>
        /// </list>
        /// </summary>
        /// <param name="activeColumns"></param>
        /// <param name="learn"></param>
        /// <returns></returns>
        /// <remarks>Note: PredictiveCells are not calculated here. They are calculated on demand from active segments.</remarks>
        public ComputeCycle Compute(int[] activeColumns, bool learn)
        {
            ComputeCycle cycle = ActivateCells(this.connections, activeColumns, learn);
            ActivateDendrites(this.connections, cycle, learn);
            return cycle;
        }

        /// <summary>
        /// Calculate the active cells, using the current active columns and dendrite segments. Grow and reinforce synapses.
        /// 
        /// <para>
        /// Pseudocode:<br/>
        ///   for each column<br/>
        ///     if column is active and has active distal dendrite segments<br/>
        ///       call activatePredictedColumn<br/>
        ///     if column is active and doesn't have active distal dendrite segments<br/>
        ///       call burstColumn<br/>
        ///     if column is inactive and has matching distal dendrite segments<br/>
        ///       call punishPredictedColumn<br/>
        /// </para>
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="activeColumnIndices"></param>
        /// <param name="learn"></param>
        /// <returns></returns>
        protected ComputeCycle ActivateCells(Connections conn, int[] activeColumnIndices, bool learn)
        {
            ComputeCycle cycle = new ComputeCycle();
            cycle.ActivColumnIndicies = activeColumnIndices;

            ColumnData activeColumnData = new ColumnData();

            ISet<Cell> prevActiveCells = conn.ActiveCells;
            ISet<Cell> prevWinnerCells = conn.WinnerCells;

            // The list of active columns.
            List<Column> activeColumns = new List<Column>();

            foreach (var indx in activeColumnIndices.OrderBy(i => i))
            {
                activeColumns.Add(conn.GetColumn(indx));
            }

            //Func<Object, Column> segToCol = segment => ((DistalDendrite)segment).getParentCell().getParentColumnIndex();

            Func<Object, Column> segToCol = (segment) =>
            {
                var colIndx = ((DistalDendrite)segment).ParentCell.ParentColumnIndex;
                var parentCol = this.connections.HtmConfig.Memory.GetColumn(colIndx);
                return parentCol;
            };

            Func<object, Column> times1Fnc = x => (Column)x;

            var list = new Pair<List<object>, Func<object, Column>>[3];
            list[0] = new Pair<List<object>, Func<object, Column>>(Array.ConvertAll(activeColumns.ToArray(), item => (object)item).ToList(), times1Fnc);
            list[1] = new Pair<List<object>, Func<object, Column>>(Array.ConvertAll(conn.ActiveSegments.ToArray(), item => (object)item).ToList(), segToCol);
            list[2] = new Pair<List<object>, Func<object, Column>>(Array.ConvertAll(conn.MatchingSegments.ToArray(), item => (object)item).ToList(), segToCol);

            GroupBy2<Column> grouper = GroupBy2<Column>.Of(list);

            double permanenceIncrement = conn.HtmConfig.PermanenceIncrement;
            double permanenceDecrement = conn.HtmConfig.PermanenceDecrement;

            //
            // Grouping by columns, which have active and matching segments.
            foreach (var tuple in grouper)
            {
                activeColumnData = activeColumnData.Set(tuple);

                if (activeColumnData.IsExistAnyActiveCol(cIndexofACTIVE_COLUMNS))
                {
                    // If there are some active segments on the column already...
                    if (activeColumnData.ActiveSegments != null && activeColumnData.ActiveSegments.Count > 0)
                    {
                        Debug.Write(".");

                        List<Cell> cellsOwnersOfActSegs = ActivatePredictedColumn(conn, activeColumnData.ActiveSegments,
                            activeColumnData.MatchingSegments, prevActiveCells, prevWinnerCells,
                                permanenceIncrement, permanenceDecrement, learn, cycle.ActiveSynapses);

                        foreach (var item in cellsOwnersOfActSegs)
                        {
                            cycle.ActiveCells.Add(item);
                            cycle.WinnerCells.Add(item);
                        }
                    }
                    else
                    {
                        Debug.Write("B.");
                        //
                        // If no active segments are detected (start of learning) then all cells are activated
                        // and a random single cell is chosen as a winner.
                        BurstingResult burstingResult = BurstColumn(conn, activeColumnData.Column(), activeColumnData.MatchingSegments,
                            prevActiveCells, prevWinnerCells, permanenceIncrement, permanenceDecrement, conn.HtmConfig.Random,
                               learn);

                        // DRAFT. Removing this as unnecessary.
                        //cycle.ActiveCells.Add(burstingResult.BestCell);

                        //
                        // Here we activate all cells by putting them to list of active cells.
                        foreach (var item in burstingResult.Cells)
                        {
                            cycle.ActiveCells.Add(item);
                        }

                        //var actSyns = conn.getReceptorSynapses(burstingResult.BestCell).Where(s=>prevActiveCells.Contains(s.SourceCell));
                        //foreach (var syn in actSyns)
                        //{
                        //    cycle.ActiveSynapses.Add(syn);
                        //}

                        cycle.WinnerCells.Add((Cell)burstingResult.BestCell);
                    }
                }
                else
                {
                    if (learn)
                    {
                        PunishPredictedColumn(conn, activeColumnData.ActiveSegments, activeColumnData.MatchingSegments,
                            prevActiveCells, prevWinnerCells, conn.HtmConfig.PredictedSegmentDecrement);
                    }
                }
            }


            //int[] arr = new int[cycle.winnerCells.Count];
            //int count = 0;
            //foreach (Cell activeCell in cycle.winnerCells)
            //{
            //    arr[count] = activeCell.Index;
            //    count++;
            //}

            return cycle;
        }

        /// <summary>
        /// Calculate dendrite segment activity, using the current active cells.
        /// 
        /// <para>
        /// Pseudocode:<br/>
        ///   for each distal dendrite segment with number of active synapses >= activationThreshold<br/>
        ///     mark the segment as active<br/>
        ///   for each distal dendrite segment with unconnected activity >= minThreshold<br/>
        ///     mark the segment as matching<br/>
        /// </para>
        /// </summary>
        /// <param name="conn">the Connectivity</param>
        /// <param name="cycle">Stores current compute cycle results</param>
        /// <param name="learn">If true, segment activations will be recorded. This information is used during segment cleanup.</param>
        protected void ActivateDendrites(Connections conn, ComputeCycle cycle, bool learn)
        {
            SegmentActivity activity = conn.ComputeActivity(cycle.ActiveCells, conn.HtmConfig.ConnectedPermanence);

            var activeSegments = new List<DistalDendrite>();
            foreach (var item in activity.ActiveSynapses)
            {
                if (item.Value >= conn.HtmConfig.ActivationThreshold)
                    activeSegments.Add(conn.GetSegmentForFlatIdx(item.Key));
            }

            //
            // Step through all synapses on active cells and find involved segments.         
            var matchingSegments = new List<DistalDendrite>();
            foreach (var item in activity.PotentialSynapses)
            {
                if (item.Value >= conn.HtmConfig.MinThreshold)
                    matchingSegments.Add(conn.GetSegmentForFlatIdx(item.Key));
            }

            //
            // Step through all synapses on active cells with permanence over threshold (conencted synapses)
            // and find involved segments.         
            activeSegments.Sort(GetComparer(conn.NextSegmentOrdinal));

            matchingSegments.Sort(GetComparer(conn.NextSegmentOrdinal));

            cycle.ActiveSegments = activeSegments;
            cycle.MatchingSegments = matchingSegments;

            conn.LastActivity = activity;
            conn.ActiveCells = new HashSet<Cell>(cycle.ActiveCells);
            conn.WinnerCells = new HashSet<Cell>(cycle.WinnerCells);
            conn.ActiveSegments = activeSegments;
            conn.MatchingSegments = matchingSegments;

            // Forces generation of the predictive cells from the above active segments
            conn.ClearPredictiveCells();
            //cycle.DepolirizeCells(conn);

            if (learn)
            {
                foreach (var segment in activeSegments)
                {
                    conn.RecordSegmentActivity(segment);
                }

                conn.StartNewIteration();
            }

            Debug.WriteLine($"\nActive segments: {activeSegments.Count}, Matching segments: {matchingSegments.Count}");
        }


        /// <summary>
        /// Indicates the start of a new sequence. 
        /// Clears any predictions and makes sure synapses don't grow to the currently active cells in the next time step.
        /// </summary>
        /// <param name="connections"></param>
        public void Reset(Connections connections)
        {
            connections.ActiveCells.Clear();
            connections.WinnerCells.Clear();
            connections.ActiveSegments.Clear();
            connections.MatchingSegments.Clear();
        }
        //TODO same documentation?
        /**
 * Determines which cells in a predicted column should be added to winner cells
 * list, and learns on the segments that correctly predicted this column.
 * 
 * @param conn                 the connections
 * @param activeSegments       Active segments in the specified column
 * @param matchingSegments     Matching segments in the specified column
 * @param prevActiveCells      Active cells in `t-1`
 * @param prevWinnerCells      Winner cells in `t-1`
 * @param learn                If true, grow and reinforce synapses
 * 
 * <pre>
 * Pseudocode:
 *   for each cell in the column that has an active distal dendrite segment
 *     mark the cell as active
 *     mark the cell as a winner cell
 *     (learning) for each active distal dendrite segment
 *       strengthen active synapses
 *       weaken inactive synapses
 *       grow synapses to previous winner cells
 * </pre>
 * 
 * @return A list of predicted cells that will be added to active cells and winner
 *         cells.
 */


        /// <summary>
        /// TM acitivates segments on the column in the previous cycle. This method locates such segments and 
        /// adapts them. 
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="columnActiveSegments">Active segments as calculated (activated) in the previous step.</param>
        /// <param name="matchingSegments"></param>
        /// <param name="prevActiveCells">Cells active in the current cycle.</param>
        /// <param name="prevWinnerCells"></param>
        /// <param name="permanenceIncrement"></param>
        /// <param name="permanenceDecrement"></param>
        /// <param name="learn"></param>
        /// <returns>Cells which own active column segments as calculated in the previous step.</returns>
        private List<Cell> ActivatePredictedColumn(Connections conn, List<DistalDendrite> columnActiveSegments,
            List<DistalDendrite> matchingSegments, ICollection<Cell> prevActiveCells, ICollection<Cell> prevWinnerCells,
                double permanenceIncrement, double permanenceDecrement, bool learn, IList<Synapse> activeSynapses)
        {
            List<Cell> cellsOwnersOfActiveSegments = new List<Cell>();
            Cell previousCell = null;
            Cell segmOwnerCell;

            foreach (DistalDendrite segment in columnActiveSegments)
            {
                foreach (Synapse synapse in new List<Synapse>(conn.GetSynapses(segment)))
                {
                    // WORKING DRAFT. TM algorithm change.
                    if (prevActiveCells.Contains(synapse.getPresynapticCell()))
                    {
                        // TODO
                        // Review this. not only previous cell should be consiered.
                        // We should rather consider all current list and look if the cell is already in.
                        segmOwnerCell = segment.ParentCell;
                        if (segmOwnerCell != previousCell)
                        {
                            //activeSynapses.Add(synapse);
                            cellsOwnersOfActiveSegments.Add(segmOwnerCell);
                            previousCell = segmOwnerCell;
                        }
                        else
                        {
                            // for debugging.
                        }

                        if (learn)
                        {
                            AdaptSegment(conn, segment, prevActiveCells, permanenceIncrement, permanenceDecrement);

                            int numActive = conn.LastActivity.PotentialSynapses[segment.SegmentIndex];
                            int nGrowDesired = conn.HtmConfig.MaxNewSynapseCount - numActive;

                            if (nGrowDesired > 0)
                            {
                                // Create new synapses on the segment from winner (pre-synaptic cells) cells.
                                GrowSynapses(conn, prevWinnerCells, segment, conn.HtmConfig.InitialPermanence,
                                    nGrowDesired, conn.HtmConfig.Random);
                            }
                        }
                    }
                }

                //if (learn)
                //{
                //    AdaptSegment(conn, segment, prevActiveCells, permanenceIncrement, permanenceDecrement);

                //    int numActive = conn.getLastActivity().PotentialSynapses[segment.getIndex()];
                //    int nGrowDesired = conn.HtmConfig.MaxNewSynapseCount - numActive;

                //    if (nGrowDesired > 0)
                //    {
                //        // Create new synapses on the segment from winner (pre-synaptic cells) cells.
                //        growSynapses(conn, prevWinnerCells, segment, conn.getInitialPermanence(),
                //            nGrowDesired, conn.getRandom());
                //    }
                //}
            }

            return cellsOwnersOfActiveSegments;
        }

        /// <summary>
        /// Activates all of the cells in an unpredicted active column,
        /// chooses a winner cell, and, if learning is turned on, either adapts or
        /// creates a segment. growSynapses is invoked on this segment.<br/>
        /// <para>
        /// <b>Pseudocode:</b><br/>
        ///  mark all cells as active<br/>
        ///  if there are any matching distal dendrite segments<br/>
        ///      find the most active matching segment<br/>
        ///      mark its cell as a winner cell<br/>
        ///      (learning)<br/>
        ///      grow and reinforce synapses to previous winner cells<br/>
        ///  else<br/>
        ///      find the cell with the least segments, mark it as a winner cell<br/>
        ///      (learning)<br/>
        ///      (optimization) if there are previous winner cells<br/>
        ///          add a segment to this winner cell<br/>
        ///          grow synapses to previous winner cells<br/>
        /// </para>
        /// </summary>
        /// <param name="conn">Connections instance for the TM</param>
        /// <param name="column">Bursting <see cref="Column"/></param>
        /// <param name="matchingSegments">List of matching <see cref="DistalDendrite"/>s</param>
        /// <param name="prevActiveCells">Active cells in `t-1`</param>
        /// <param name="prevWinnerCells">Winner cells in `t-1`</param>
        /// <param name="permanenceIncrement">Amount by which permanences of synapses are decremented during learning</param>
        /// <param name="permanenceDecrement">Amount by which permanences of synapses are incremented during learning</param>
        /// <param name="random">Random number generator</param>
        /// <param name="learn">Whether or not learning is enabled</param>
        /// <returns>
        /// <see cref="BurstingResult"/> contains two properties:<br/>
        /// Cells:      list of the processed column's cells<br/>
        /// BestCell:    the best cell
        /// </returns>
        public BurstingResult BurstColumn(Connections conn, Column column, List<DistalDendrite> matchingSegments,
            ICollection<Cell> prevActiveCells, ICollection<Cell> prevWinnerCells, double permanenceIncrement, double permanenceDecrement,
                Random random, bool learn)
        {

            IList<Cell> cells = column.Cells;
            Cell leastUsedCell = null;

            //
            // Matching segments result from number of potential synapses. These are segments with number of potential
            // synapses permanence higher than some minimum threshold value.
            // Potential synapses are synapses from presynaptc cells connected to the active cell.
            // In other words, synapse permanence between presynaptic cell and the active cell defines a statistical prediction that active cell will become the active in the next cycle.
            // Bursting will create new segments if there are no matching segments until some matching segments appear. 
            // Once that happen, segment adoption will start.
            // If some matching segments exist, bursting will grab the segment with most potential synapses and adapt it.
            if (matchingSegments != null && matchingSegments.Count > 0)
            {
                Debug.Write($"({matchingSegments.Count})");

                DistalDendrite maxPotentialSeg = GetSegmentwithHighesPotential(conn, matchingSegments, prevActiveCells);

                // TODO what is this loop doing?
                for (int i = 0; i < matchingSegments.Count; i++)
                {
                    var temp = matchingSegments[i].SegmentIndex;
                }

                leastUsedCell = maxPotentialSeg.ParentCell;

                if (learn)
                {
                    AdaptSegment(conn, maxPotentialSeg, prevActiveCells, permanenceIncrement, permanenceDecrement);

                    int nGrowDesired = conn.HtmConfig.MaxNewSynapseCount - conn.LastActivity.PotentialSynapses[maxPotentialSeg.SegmentIndex];

                    if (nGrowDesired > 0)
                    {
                        GrowSynapses(conn, prevWinnerCells, maxPotentialSeg, conn.HtmConfig.InitialPermanence,
                            nGrowDesired, random);
                    }
                }
            }
            else
            {
                leastUsedCell = this.GetLeastUsedCell(conn, cells, random);
                if (learn)
                {
                    int nGrowExact = Math.Min(conn.HtmConfig.MaxNewSynapseCount, prevWinnerCells.Count);
                    if (nGrowExact > 0)
                    {
                        DistalDendrite bestSegment = conn.CreateDistalSegment(leastUsedCell);
                        GrowSynapses(conn, prevWinnerCells, bestSegment, conn.HtmConfig.InitialPermanence,
                            nGrowExact, random);
                    }
                }
            }

            return new BurstingResult(cells, leastUsedCell);
        }

        private int indxOfLastHighestSegment = -1;

        /// <summary>
        /// Gets the segment with maximal potential. Segment's potential is measured by number of potential synapses.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="matchingSegments"></param>
        /// <returns></returns>
        private DistalDendrite GetSegmentwithHighesPotential(Connections conn, List<DistalDendrite> matchingSegments, ICollection<Cell> prevActiveCells)
        {
            DistalDendrite maxSeg = matchingSegments[0];

            for (int i = 0; i < matchingSegments.Count - 1; i++)
            {
                var potSynsPlus1 = conn.LastActivity.PotentialSynapses[matchingSegments[i + 1].SegmentIndex];

                if (potSynsPlus1 > conn.LastActivity.PotentialSynapses[matchingSegments[i].SegmentIndex])
                {
                    //prevActiveCells.Contains(synapse.getPresynapticCell())
                    //if (matchingSegments[i + 1].getIndex() != indxOfLastHighestSegment)
                    {
                        // DRAFT
                        maxSeg = matchingSegments[i + 1];
                        indxOfLastHighestSegment = matchingSegments[i + 1].SegmentIndex;
                    }
                    //else
                    //{

                    //}
                }
            }
            return maxSeg;
        }

        /// <summary>
        /// Punishes the Segments that incorrectly predicted a column to be active.
        /// <para>
        /// Pseudocode:<br/>
        ///  for each matching segment in the column<br/>
        ///    weaken active synapses<br/>
        /// </para>
        /// </summary>
        /// <param name="conn">Connections instance for the <see cref="TemporalMemory"/></param>
        /// <param name="activeSegments">An iterable of <see cref="DistalDendrite"/> actives</param>
        /// <param name="matchingSegments">An iterable of <see cref="DistalDendrite"/> matching for the column compute is operating on that are matching; None if empty</param>
        /// <param name="prevActiveCells">Active cells in `t-1`</param>
        /// <param name="prevWinnerCells">Winner cells in `t-1` are decremented during learning.</param>
        /// <param name="predictedSegmentDecrement">Amount by which segments are punished for incorrect predictions</param>
        public void PunishPredictedColumn(Connections conn, List<DistalDendrite> activeSegments,
            List<DistalDendrite> matchingSegments, ICollection<Cell> prevActiveCells, ICollection<Cell> prevWinnerCells,
               double predictedSegmentDecrement)
        {

            if (predictedSegmentDecrement > 0)
            {
                foreach (DistalDendrite segment in matchingSegments)
                {
                    AdaptSegment(conn, segment, prevActiveCells, -conn.HtmConfig.PredictedSegmentDecrement, 0);
                }
            }
        }

        #region Helper Methods
        ////////////////////////////
        //     Helper Methods     //
        ////////////////////////////


        /// <summary>
        /// Gets the cell with the smallest number of segments.
        /// </summary>
        /// <param name="conn">Connections instance currentlly in use.</param>
        /// <param name="cells">List of cells.</param>
        /// <param name="random">Random generator.</param>
        /// <returns></returns>
        public Cell GetLeastUsedCell(Connections conn, IList<Cell> cells, Random random)
        {
            List<Cell> leastUsedCells = new List<Cell>();
            int minNumSegments = Integer.MaxValue;
            foreach (Cell cell in cells)
            {
                int numSegments = conn.NumSegments(cell);

                if (numSegments < minNumSegments)
                {
                    minNumSegments = numSegments;
                    leastUsedCells.Clear();
                }

                if (numSegments == minNumSegments)
                {
                    leastUsedCells.Add(cell);
                }
            }
            random = new Random();
            int i = random.Next(leastUsedCells.Count);
            return leastUsedCells[i];
        }

        /// <summary>
        /// Creates nDesiredNewSynapes synapses on the segment passed in if possible, choosing random cells from the previous winner cells that are
        /// not already on the segment.
        /// </summary>
        /// <param name="conn">Connections instance for the <see cref="TemporalMemory"/></param>
        /// <param name="prevWinnerCells">Winner cells in `t-1`</param>
        /// <param name="segment">Segment to grow synapses on. </param>
        /// <param name="initialPermanence">Initial permanence of a new synapse.</param>
        /// <param name="nDesiredNewSynapses">Desired number of synapses to grow</param>
        /// <param name="random"><see cref="TemporalMemory"/> object used to generate random numbers</param>
        /// <remarks>
        /// <b>Notes:</b> The process of writing the last value into the index in the array that was most recently changed is to ensure the same results that 
        /// we get in the c++ implementation using iter_swap with vectors.
        /// </remarks>
        public void GrowSynapses(Connections conn, ICollection<Cell> prevWinnerCells, DistalDendrite segment,
            double initialPermanence, int nDesiredNewSynapses, Random random)
        {
            random = new Random();
            List<Cell> removingCandidates = new List<Cell>(prevWinnerCells);
            removingCandidates = removingCandidates.OrderBy(c => c).ToList();

            //
            // Enumarates all synapses in a segment and remove winner-cells from
            // list of removingCandidates if they are presynaptic winners cells.
            // So, we will recreate only synapses on cells, which are not winners in the previous step.
            foreach (Synapse synapse in conn.GetSynapses(segment))
            {
                Cell presynapticCell = synapse.getPresynapticCell();
                int index = removingCandidates.IndexOf(presynapticCell);
                if (index != -1)
                {
                    removingCandidates.RemoveAt(index);
                }
            }

            int candidatesLength = removingCandidates.Count();

            // We take here eather wanted growing number of desired synapes of num of candidates
            // if too many growing synapses requested.
            int nActual = nDesiredNewSynapses < candidatesLength ? nDesiredNewSynapses : candidatesLength;

            //
            // Finally we randomly create new synapses. 
            for (int i = 0; i < nActual; i++)
            {
                int rndIndex = random.Next(removingCandidates.Count());
                conn.CreateSynapse(segment, removingCandidates[rndIndex], initialPermanence);
                removingCandidates.RemoveAt(rndIndex);
            }
        }



        /// <summary>
        /// Increments the permanence of the segment's synapse if the synapse's presynaptic cell 
        /// was active in the previous cycle.
        /// If it was not active, then it will decrement the permanence value. 
        /// If the permamence is below EPSILON, synapse is destroyed.
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="segment">The segment to adapt.</param>
        /// <param name="prevActiveCells">List of active cells in the current cycle (calculated in the previous cycle).</param>
        /// <param name="permanenceIncrement"></param>
        /// <param name="permanenceDecrement"></param>
        public void AdaptSegment(Connections conn, DistalDendrite segment, ICollection<Cell> prevActiveCells,
            double permanenceIncrement, double permanenceDecrement)
        {

            // Destroying a synapse modifies the set that we're iterating through.
            List<Synapse> synapsesToDestroy = new List<Synapse>();

            foreach (Synapse synapse in conn.GetSynapses(segment))
            {
                double permanence = synapse.Permanence;

                //
                // If synapse's presynaptic cell was active in the previous cycle then streng it.
                if (prevActiveCells.Contains(synapse.getPresynapticCell()))
                {
                    permanence += permanenceIncrement;
                }
                else
                {
                    permanence -= permanenceDecrement;
                }

                // Keep permanence within min/max bounds
                permanence = permanence < 0 ? 0 : permanence > 1.0 ? 1.0 : permanence;

                // Use this to examine issues caused by subtle floating point differences
                // be careful to set the scale (1 below) to the max significant digits right of the decimal point
                // between the permanenceIncrement and initialPermanence
                //
                // permanence = new BigDecimal(permanence).setScale(1, RoundingMode.HALF_UP).doubleValue(); 

                if (permanence < EPSILON)
                {
                    synapsesToDestroy.Add(synapse);
                }
                else
                {
                    synapse.Permanence = permanence;
                }
            }

            foreach (Synapse s in synapsesToDestroy)
            {
                conn.DestroySynapse(s, segment);
            }

            if (conn.GetNumSynapses(segment) == 0)
            {
                conn.DestroySegment(segment);
            }
        }
        #endregion

        /**
         * Used in the {@link TemporalMemory#compute(Connections, int[], boolean)} method
         * to make pulling values out of the {@link GroupBy2} more readable and named.
         */
        /// <summary>
        /// Used in the <see cref="Compute(int[], bool)"/> method to make pulling values out of the <see cref="GroupBy2{R}"/> more readable and named.
        /// </summary>
        public class ColumnData
        {
            private Pair<Column, List<List<Object>>> m_Pair;

            public ColumnData() { }


            public ColumnData Set(Pair<Column, List<List<Object>>> t)
            {
                m_Pair = t;

                return this;
            }

            public Column Column() { return (Column)m_Pair.Key; }

            public List<Column> ActiveColumns() { return (List<Column>)m_Pair.Value[0].Cast<Column>(); }

            public List<DistalDendrite> ActiveSegments
            {
                get
                {
                    if (m_Pair.Value.Count == 0 ||
                        m_Pair.Value[1].Count == 0)
                        return new List<DistalDendrite>();
                    else
                        return m_Pair.Value[1].Cast<DistalDendrite>().ToList();
                }
            }

            public List<DistalDendrite> MatchingSegments
            {
                get
                {
                    if (m_Pair.Value.Count == 0 ||
                         m_Pair.Value[2].Count == 0)
                        return new List<DistalDendrite>();
                    else
                        return m_Pair.Value[2].Cast<DistalDendrite>().ToList();
                }
            }


            /// <summary>
            /// Result indicates whether the slot at the specified index is empty</summary>
            /// indicator.<param name="memberIndex">Index of slot.</param>
            /// <returns></returns>
            public bool IsExistAnyActiveCol(int memberIndex)
            {
                if (m_Pair.Value.Count == 0 ||
                    m_Pair.Value[memberIndex].Count == 0)
                    return false;
                else
                    return true;
            }
        }

        public DentriteComparer GetComparer(int nextSegmentOrdinal)
        {
            return new DentriteComparer(nextSegmentOrdinal);
        }
    }
}
