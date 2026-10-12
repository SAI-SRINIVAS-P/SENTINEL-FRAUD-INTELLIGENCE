# Dataset setup

The project uses the ULB / Worldline credit-card fraud benchmark. The public dataset has 284,807 transactions from September 2013, including 492 fraud examples (about 0.172%). Its features are `Time`, `V1` through `V28`, `Amount`, and the binary `Class` label. The 28 `V` columns are anonymized PCA components, not original customer or merchant fields.

Source: https://www.kaggle.com/datasets/mlg-ulb/creditcardfraud
Alternative archive: https://zenodo.org/records/7395559

1. Obtain `creditcard.csv` under the source site's terms.
2. Place it at `data/raw/creditcard.csv` in this repository.
3. Do not commit the file. It is large and `.gitignore` excludes it.
4. Start the stack and use **Model registry → Retrain model** as the configured admin, or call `POST /api/models/retrain` with an admin JWT.

The CSV reader expects the standard 31-column header (`Time,V1,...,V28,Amount,Class`) and numeric rows. Class labels may be written as `0`, `1`, `0.0`, or `1.0` (quoted numeric fields are also normalized). If retraining reports an invalid class label, the error now includes the actual value found; the file may be a different fraud dataset, contain a repeated header, or have been exported with a different schema. It sorts rows by the `Time` feature and splits chronologically: the first 70% for training, the next 15% for threshold selection, and the final 15% for metrics. Analyst-reviewed labels are added only to the training partition. No synthetic score, pretrained model, or benchmark metric is shipped with the project.

## Interpret results carefully

- PR-AUC is reported on the final chronological holdout; precision and recall are measured at the threshold selected using the validation slice. Precision and recall shown in the registry are the training-time snapshot. Changing the live threshold changes routing decisions but does not retroactively recompute holdout metrics.
- The feature explanation shown on each scored transaction is single-feature ablation sensitivity: one field is set to zero and its effect on model probability is measured. It is not SHAP, causal attribution, or proof of why a transaction is fraudulent.
- The drift signal compares recent per-feature means to training means using training standard deviations. It is a lightweight univariate screen, not a formal multivariate drift test.
- This historical benchmark is suitable for demonstrating data and model engineering. It does not provide the real-time behavioral, merchant, device, or customer history features needed for a production fraud system.
