﻿using System.Collections.Generic;
using System.Linq;

public class PermanenceThreshold
{
    public static double ApplyThreshold(double permanence)
    {
        const double threshold = 0.5;

        // Apply threshold logic
        return permanence < threshold ? 0.0 : 1.0;
    }
    public static Dictionary<int, double> ApplyThreshold(Dictionary<int, double> permanences)
    {
        // Apply threshold logic to each value in the dictionary
        return permanences.ToDictionary(kvp => kvp.Key, kvp => ApplyThreshold(kvp.Value));
    }
}