using System.Globalization;
using System.Text.Json;
using Fraud.Api.Models;

namespace Fraud.Api.Services;

public static class TrainingDataReader
{
    public static readonly string[] FeatureNames =
        ["Time", .. Enumerable.Range(1, 28).Select(i => $"V{i}"), "Amount"];

    // The ULB file is a simple numeric CSV: Time,V1..V28,Amount,Class.
    // The dataset is not redistributed in this repository. Keep the source file outside git.
    public static List<TrainingExample> ReadUlbCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dataset not found at '{path}'. See docs/DATASET.md.", path);

        var rows = new List<TrainingExample>();
        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null)
            throw new InvalidDataException("The training CSV is empty. Expected the ULB credit-card CSV with 31 columns and a final Class column.");

        var header = headerLine.Split(',').Select(NormalizeCsvField).ToArray();
        string[] expectedHeader = ["Time", .. Enumerable.Range(1, 28).Select(i => $"V{i}"), "Amount", "Class"];
        if (header.Length != expectedHeader.Length ||
            !header.SequenceEqual(expectedHeader, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Unexpected CSV header. Expected the ULB / Worldline creditcard.csv format: Time,V1,...,V28,Amount,Class (31 columns). " +
                $"Found {header.Length} columns with final header '{(header.Length > 0 ? header[^1] : "<empty>")}'. " +
                "Make sure you placed the original creditcard.csv file at data/raw/creditcard.csv, not a different fraud dataset or a report/export file.");
        }

        string? line;
        var lineNumber = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var columns = line.Split(',').Select(NormalizeCsvField).ToArray();
            if (columns.Length != expectedHeader.Length)
                throw new InvalidDataException($"Expected exactly 31 columns at CSV line {lineNumber}, found {columns.Length}. Check that the file is the original ULB creditcard.csv and is comma-delimited.");

            var features = new float[30];
            for (var i = 0; i < features.Length; i++)
            {
                if (!float.TryParse(columns[i], NumberStyles.Float, CultureInfo.InvariantCulture, out features[i]) ||
                    !float.IsFinite(features[i]))
                    throw new InvalidDataException($"Invalid numeric feature '{columns[i]}' at CSV line {lineNumber}, column {i + 1} ({expectedHeader[i]}).");
            }

            // Some CSV exporters serialize numeric class labels as 0.0 / 1.0 or quote them.
            // Those are valid encodings of the ULB dataset's binary labels, so normalize before validating.
            if (!float.TryParse(columns[30], NumberStyles.Float, CultureInfo.InvariantCulture, out var numericLabel) ||
                !float.IsFinite(numericLabel) || (numericLabel != 0f && numericLabel != 1f))
            {
                throw new InvalidDataException(
                    $"Invalid Class label '{columns[30]}' at CSV line {lineNumber}. Expected numeric 0 or 1 (also accepts 0.0 / 1.0). " +
                    "If the value is 'Class', the header may be repeated inside the file; if it is text, you may have downloaded a different dataset. " +
                    "Use the original ULB / Worldline creditcard.csv with columns Time,V1,...,V28,Amount,Class.");
            }

            rows.Add(new TrainingExample { Features = features, Label = numericLabel == 1f });
        }

        if (rows.Count < 1000)
            throw new InvalidDataException($"Dataset has only {rows.Count} rows. Use the full ULB credit-card CSV (at least 1,000 rows). ");
        if (rows.Count(x => x.Label) < 20)
            throw new InvalidDataException("Dataset has fewer than 20 positive fraud labels; holdout metrics would be unstable.");
        // Use the Time feature rather than assuming the input file rows are already ordered.
        return rows.OrderBy(row => row.Features[0]).ToList();
    }

    private static string NormalizeCsvField(string value) =>
        value.Trim().Trim('\uFEFF').Trim().Trim('"').Trim();

    public static float[] DeserializeFeatures(string json)
    {
        var features = JsonSerializer.Deserialize<float[]>(json);
        if (features is null || features.Length != 30 || features.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Stored feedback does not contain exactly 30 finite features.");
        return features;
    }
}
