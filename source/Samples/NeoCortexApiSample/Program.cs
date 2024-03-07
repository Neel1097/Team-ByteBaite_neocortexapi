﻿using NeoCortexApi;
using NeoCortexApi.Encoders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace NeoCortexApiSample
{
    class Program
    {
        /// <summary>
        /// This sample shows a typical experiment code for SP and TM.
        /// You must start this code in debugger to follow the trace.
        /// and TM.
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
           
            // Starts experiment that demonstrates how to learn spatial patterns.
            SpatialPatternLearning experiment = new SpatialPatternLearning();
            experiment.Run();

        }

       
    }
}
