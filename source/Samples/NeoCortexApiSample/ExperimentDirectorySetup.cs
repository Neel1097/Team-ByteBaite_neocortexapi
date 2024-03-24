﻿
using System;
using System.IO;

public class ExperimentDirectorySetup
{
    private readonly string experimentName;

    public ExperimentDirectorySetup(string experimentName)
    {
        this.experimentName = experimentName;
    }

    public string SetupExperimentDirectory()
    {
        string outFolder = experimentName;
        string outputDirectory = Path.Combine(Environment.CurrentDirectory, outFolder);

        try
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, true);
            }

            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            // Log or handle the exception appropriately
            Console.WriteLine($"Error creating directory: {ex.Message}");
        }

        return outputDirectory;
    }
}
