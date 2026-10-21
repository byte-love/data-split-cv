using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DemoTextClient.Utils.SqliteConnectUtils
{
    /// <summary>单个类别的指标（一行）</summary>
    public class ClassRow
    {
        public string Label = "";
        public double Precision = double.NaN;
        public double Recall = double.NaN;
        public double F1 = double.NaN;
        public long Support;      // 跨折平均时 = 各折求和（折互不重叠 → 正好等于该类在全部数据里的条数）
        public int FoldCount;     // 有几折出现过这个类别（某折考卷里没这个类时，那一折不参与该类平均）
    }

    /// <summary>某一折（某次重复里的某一折）的评估结果</summary>
    public class FoldMetric
    {
        public int Repeat = 1;          // 第几轮重复（十折交叉验证用；其它方法恒为 1）
        public int Fold = 1;            // 第几折（从 1 开始）
        public int TrainCount;          // 该折训练集条数
        public int TestCount;           // 该折测试集条数
        public double Accuracy = double.NaN;
        public double MacroF1 = double.NaN;
        public double WeightedF1 = double.NaN;
        public bool Parsed;             // 逐折报告是否解析成功
        public string Message = "";     // 失败/警告原因
        public List<ClassRow> Classes = new List<ClassRow>();   // 该折的逐类指标（结果页下半部分的来源）

        public string Display
        {
            get
            {
                string head = Repeat > 1 ? $"第{Repeat}轮 第{Fold}折" : $"第{Fold}折";
                if (!Parsed)
                {
                    return $"{head}：未取到指标（{Message}）｜训练 {TrainCount} 条 / 测试 {TestCount} 条";
                }
                return $"{head}：准确率 {N(Accuracy)}，"
                     + $"Macro F1 {N(MacroF1)}，"
                     + $"Weighted F1 {N(WeightedF1)}"
                     + $"｜训练 {TrainCount} 条 / 测试 {TestCount} 条";
            }
        }

        private static string N(double v)
        {
            return v.ToString("F4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>一组指标的平均值 ± 波动</summary>
    public class MetricSummary
    {
        public int Count;
        public double Mean = double.NaN;
        public double Std = double.NaN;     // 总体标准差
        public double Min = double.NaN;
        public double Max = double.NaN;

        public bool HasValue { get { return Count > 0; } }

        /// <summary>留一：这个值不是"各折平均"，而是把全部折外预测汇总成一张混淆矩阵算出来的</summary>
        public bool Pooled;
        public int PooledSamples;       //参与汇总的折外预测条数

        public string Display
        {
            get
            {
                if (!HasValue) return "—";
                if (Pooled) return $"{N(Mean)}（{PooledSamples} 条折外预测汇总）";
                return $"{N(Mean)} ± {N(Std)}（{N(Min)} ~ {N(Max)}，{Count} 折）";
            }
        }

        private static string N(double v)
        {
            return v.ToString("F4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>交叉验证的全套汇总</summary>
    public class CrossValidationSummary
    {
        public string MethodName = "";
        public int FoldCount;
        public int RepeatCount = 1;
        public int TotalRuns;               // 实际跑了多少次训练
        public int FailedRuns;              // 没取到指标的次数
        public int SampleCount;             // 参与交叉验证的总条数
        public List<string> SingleSampleLabels = new List<string>();   // 只有 1 条样本的标签（会导致某些折训练集缺标签）
        public List<string> KnownLabels = new List<string>();
        public MetricSummary Accuracy = new MetricSummary();
        public MetricSummary MacroF1 = new MetricSummary();
        public MetricSummary WeightedF1 = new MetricSummary();
        public List<FoldMetric> Metrics = new List<FoldMetric>();
        public List<ClassRow> ClassAverages = new List<ClassRow>();   // 逐类指标（K折/十折 = 跨折平均；留一 = 折外预测汇总）
        public bool PooledLoo;              // 留一：主指标与逐类指标都按"折外预测汇总"算（不是各折平均）
        public int PooledSampleCount;       // 参与汇总的折外预测条数

        public string BuildText()
        {
            var sb = new StringBuilder();
            string labelList = string.Join(" / ", KnownLabels);
            sb.AppendLine("===== 交叉验证结果 =====");
            sb.AppendLine($"划分方法：{MethodName}");
            sb.AppendLine($"参与数据：{SampleCount} 条；标签 {KnownLabels.Count} 个（{labelList}）");
            if (RepeatCount > 1 && FoldCount > 1)
            {
                sb.AppendLine($"执行方式：{FoldCount} 折 × 重复 {RepeatCount} 轮 = 共训练 {TotalRuns} 次");
            }
            else
            {
                sb.AppendLine($"执行方式：共训练 {TotalRuns} 次");
            }
            sb.AppendLine();
            sb.AppendLine("逐次明细：");
            foreach (var m in Metrics)
            {
                sb.AppendLine("  " + m.Display);
            }
            sb.AppendLine();
            if (PooledLoo)
            {
                sb.AppendLine($"汇总（留一：把 {PooledSampleCount} 条折外预测汇总成一张混淆矩阵后计算 —— "
                            + "每条数据都被「没见过它」的模型预测过一次，这样算出来才等于教科书留一）：");
            }
            else
            {
                sb.AppendLine("汇总（平均 ± 波动）：");
            }
            sb.AppendLine($"  准确率：{Accuracy.Display}");
            sb.AppendLine($"  Macro F1：{MacroF1.Display}");
            sb.AppendLine($"  Weighted F1：{WeightedF1.Display}");
            if (FailedRuns > 0)
            {
                sb.AppendLine($"  ⚠ 有 {FailedRuns} 次没取到指标，已从汇总里剔除（明细里标了原因）");
                sb.AppendLine("     （最常见原因：该次测试集只覆盖 1 个类别 —— 内核用 sklearn 的 classification_report 出报告，"
                            + "测试集只有一类时会报 target_names 不匹配，内核就不写报告了；留一/小数据最容易碰到。"
                            + "留一每折只有 1 条测试样本，所以留一的平均指标会明显偏低、不建议作为结论。）");
            }
            if (SingleSampleLabels.Count > 0)
            {
                string single = string.Join(" / ", SingleSampleLabels);
                sb.AppendLine($"  ⚠ 标签 {single} 只有 1 条样本，"
                            + "分到哪一折时该折的训练集就会缺这个标签，那一次指标会失真（内核会直接给 0）；建议每个标签至少 2 条。");
            }
            if (ClassAverages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(PooledLoo
                    ? "逐类指标（留一：按折外预测汇总；support = 该类总条数）："
                    : "逐类指标（各折平均；support 为各折求和 = 该类总条数）：");
                foreach (var c in ClassAverages)
                {
                    sb.AppendLine("  " + c.Label + "：precision " + Num(c.Precision)
                                + "，recall " + Num(c.Recall)
                                + "，f1-score " + Num(c.F1)
                                + "，support " + c.Support.ToString(CultureInfo.InvariantCulture)
                                + (PooledLoo ? "" : "（" + c.FoldCount + " 折参与平均）"));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 生成结果页要读的报告（**整份覆盖写**）。
        /// 客户端解析这个文件时是 `Split("#BYTELOVE#")[0]` 再按行**从下标 2 开始**扫：
        ///   · 前两行会被跳过 → 正好放标题和说明
        ///   · 第 3~5 行是三个总指标（= 交叉验证平均值）
        ///   · 后面每行"类别名 + 4 个数字"就是结果页下半部分的逐类明细（= 各折平均）
        /// 注意：D1 之后内核不写报告了（最终模型不留测试集），这份报告完全由客户端生成。
        /// </summary>
        public string BuildReportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(PooledLoo
                ? $"===== {MethodName}结果（主指标与逐类指标均为「折外预测汇总」）====="
                : $"===== {MethodName}结果（主指标与逐类指标均为各折平均值）=====");
            sb.AppendLine(PooledLoo
                ? $"共 {TotalRuns} 次训练，失败 {FailedRuns} 次；留一：把 {PooledSampleCount} 条折外预测汇总成一张混淆矩阵后计算，support 为该类总条数"
                : $"共 {TotalRuns} 次训练，失败 {FailedRuns} 次；逐类指标为各折平均，support 为各折求和");
            sb.AppendLine($"整体准确率accuracy: {Num(Accuracy.Mean)}");
            sb.AppendLine($"Macro F1 Score: {Num(MacroF1.Mean)}");
            sb.AppendLine($"Weighted F1 Score: {Num(WeightedF1.Mean)}");
            sb.AppendLine();
            sb.Append(CrossValidationUtils.RenderClassTable(ClassAverages));
            return sb.ToString();
        }

        private static string Num(double v)
        {
            if (double.IsNaN(v)) return "0.0000";
            return v.ToString("F4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 四种数据划分方法里的后三种（K折 / 留一 / 十折）所需的切分与汇总。
    /// 设计原则：**不改训练内核**——内核只会「用 training_set 训练、用 testing_set 评估」，
    /// 所以这里按折把临时库 Resources\BigModel\SqliteData_Temp.sqlite 里的
    /// training_set / testing_set 重新摆好，再让上层循环调用现成的 exe。
    /// 全部数据先快照到 cv_source，保证每折都在同一份全量数据上切，不会越跑越少。
    /// </summary>
    public static class CrossValidationUtils
    {
        private const string SourceTable = "cv_source";
        private const string FoldMapTable = "cv_fold_map";

        //计时（日志里报"每折用时 / 预计还需"用；交叉验证开始置位、结束复位）
        private static DateTime _foldStartTime = DateTime.MinValue;
        private static DateTime _totalStartTime = DateTime.MinValue;

        private class IdLabel
        {
            public long Id { get; set; }
            public string Label { get; set; }
        }

        /// <summary>把当前 training_set 全量快照到 cv_source（调用前应先清空 testing_set）</summary>
        public static int SnapshotSource()
        {
            using (var conn = TempSqliteUtils.GetTempConnection())
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    conn.Execute($"DROP TABLE IF EXISTS [{SourceTable}]", transaction: tx);
                    conn.Execute($"CREATE TABLE [{SourceTable}] AS SELECT * FROM [training_set]", transaction: tx);
                    conn.Execute($"CREATE INDEX IF NOT EXISTS idx_cv_source_id ON [{SourceTable}](Id)", transaction: tx);
                    int n = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM [{SourceTable}]", transaction: tx);
                    tx.Commit();
                    return n;
                }
            }
        }

        /// <summary>清掉快照与折映射（交叉验证结束、取消时都要调）</summary>
        public static void DropSnapshot()
        {
            _foldStartTime = DateTime.MinValue;
            _totalStartTime = DateTime.MinValue;
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    conn.Execute($"DROP TABLE IF EXISTS [{SourceTable}]");
                    conn.Execute($"DROP TABLE IF EXISTS [{FoldMapTable}]");
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>快照里的全部标签（按名称排序，与内核 sorted(label_set) 的口径一致）</summary>
        public static List<string> GetLabels()
        {
            using (var conn = TempSqliteUtils.GetTempConnection())
            {
                conn.Open();
                var list = conn.Query<string>(
                    $"SELECT DISTINCT IFNULL(TextLabelName,'') FROM [{SourceTable}]").ToList();
                return list.Where(x => x != null && x.Trim().Length > 0).OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
        }

        /// <summary>只有 1 条样本的标签——分到哪一折，那一折的训练集就会缺这个标签</summary>
        public static List<string> GetSingleSampleLabels()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    return conn.Query<string>(
                        $@"SELECT IFNULL(TextLabelName,'') AS L FROM [{SourceTable}]
                           WHERE IFNULL(TextLabelName,'') <> ''
                           GROUP BY IFNULL(TextLabelName,'') HAVING COUNT(*) = 1").ToList();
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>每个标签的样本数里的最小值（0 = 没有标签）。K 折数不能超过它，否则会有折缺标签甚至整折为空</summary>
        public static int GetMinLabelCount()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    var v = conn.ExecuteScalar<int?>(
                        $@"SELECT MIN(c) FROM (SELECT COUNT(*) AS c FROM [{SourceTable}]
                                               WHERE IFNULL(TextLabelName,'') <> ''
                                               GROUP BY IFNULL(TextLabelName,''))");
                    return v.HasValue ? v.Value : 0;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 生成折映射。随机数用固定种子 → 同一 seed 下切法完全可复现。
        /// · leaveOneOut = true：**留一**。标准定义是"每条样本各当一次测试集"，所以**不按标签分块**，
        ///   每折正好 1 条样本（共 N 折）。
        ///   ⚠ 原来按标签分块是错的：2 个标签各 5 条、切 10 折时，每个标签只覆盖 5 个连续的折槽，
        ///   于是出现"1 条的折 / 2 条的折 / 0 条的折"（2026-09-29 实测：第 1 折 1 条、第 2 折 2 条……
        ///   第 7 折空 → 抛"本折切出来是空的（训练 10 条 / 测试 0 条）"，整轮交叉验证中断）。
        /// · leaveOneOut = false：**K折/十折**。按标签分层，同标签内先打乱、再轮流发牌（各标签各自随机
        ///   一个起始折）→ 每折各类别比例一致、样本量尽量均匀；最后再兜一层"不许有空折"。
        /// </summary>
        public static void BuildFoldMap(int foldCount, int seed, bool leaveOneOut = false)
        {
            if (foldCount < 2) foldCount = 2;

            List<IdLabel> rows;
            using (var conn = TempSqliteUtils.GetTempConnection())
            {
                conn.Open();
                rows = conn.Query<IdLabel>(
                    $"SELECT Id, IFNULL(TextLabelName,'') AS Label FROM [{SourceTable}] ORDER BY Id").ToList();
            }

            var rnd = new Random(seed);
            var map = new List<IdLabel2>();

            if (leaveOneOut)
            {
                //留一：先把所有样本整体打乱（保证"每次留哪一条"是随机的、且可复现），再一条一折
                var all = rows.OrderBy(r => r.Id).ToList();
                for (int i = all.Count - 1; i > 0; i--)
                {
                    int j = rnd.Next(i + 1);
                    var tmp = all[i]; all[i] = all[j]; all[j] = tmp;
                }
                for (int i = 0; i < all.Count; i++)
                {
                    map.Add(new IdLabel2 { Id = all[i].Id, FoldIndex = i });
                }
            }
            else
            {
                foreach (var group in rows.GroupBy(r => r.Label ?? ""))
                {
                    var list = group.OrderBy(r => r.Id).ToList();
                    for (int i = list.Count - 1; i > 0; i--)   // 同一标签内 Fisher-Yates 洗牌
                    {
                        int j = rnd.Next(i + 1);
                        var tmp = list[i]; list[i] = list[j]; list[j] = tmp;
                    }
                    int offset = rnd.Next(foldCount);          // 该标签的起始折：否则余数会全堆到前几折
                    for (int i = 0; i < list.Count; i++)
                    {
                        map.Add(new IdLabel2 { Id = list[i].Id, FoldIndex = (i + offset) % foldCount });
                    }
                }
                RebalanceEmptyFolds(map, foldCount, rnd);
            }

            using (var conn = TempSqliteUtils.GetTempConnection())
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    conn.Execute($"DROP TABLE IF EXISTS [{FoldMapTable}]", transaction: tx);
                    conn.Execute($"CREATE TABLE [{FoldMapTable}](Id INTEGER PRIMARY KEY, FoldIndex INTEGER)", transaction: tx);
                    conn.Execute($"INSERT INTO [{FoldMapTable}](Id, FoldIndex) VALUES (@Id, @FoldIndex)", map, tx);
                    tx.Commit();
                }
            }
        }

        private class IdLabel2
        {
            public long Id { get; set; }
            public int FoldIndex { get; set; }
        }

        /// <summary>
        /// 兜底：把空折填上。K 比"最小类样本数"还大时，分层发牌可能留下一条都没有的折；
        /// 空折会让 ApplyFold 抛异常、整轮交叉验证中断，所以这里从样本最多的折挪一条过来。
        /// </summary>
        private static void RebalanceEmptyFolds(List<IdLabel2> map, int foldCount, Random rnd)
        {
            if (map == null || map.Count == 0) return;

            for (int guard = 0; guard < foldCount * 2; guard++)
            {
                var counts = new int[foldCount];
                foreach (var m in map) counts[m.FoldIndex]++;

                int empty = -1, most = -1, mostCount = 0;
                for (int f = 0; f < foldCount; f++)
                {
                    if (counts[f] == 0 && empty < 0) empty = f;
                    if (counts[f] > mostCount) { mostCount = counts[f]; most = f; }
                }
                if (empty < 0) return;                 // 没有空折 → 收工
                if (mostCount <= 1) return;            // 每条都独占一折，没得可挪（正常不会走到）

                var movable = map.Where(x => x.FoldIndex == most).ToList();
                var pick = movable[rnd.Next(movable.Count)];
                pick.FoldIndex = empty;
            }
        }

        /// <summary>
        /// 把第 foldIndex 折摆到 testing_set、其余全部摆到 training_set（两表都从快照重建，可反复调用）。
        /// foldIndex 从 0 开始。返回该折训练/测试条数。
        /// </summary>
        public static void ApplyFold(int foldIndex, out int trainCount, out int testCount)
        {
            trainCount = 0;
            testCount = 0;

            //本折开始计时（第一次调用时同时记下整轮交叉验证的起点）
            _foldStartTime = DateTime.Now;
            if (_totalStartTime == DateTime.MinValue) _totalStartTime = _foldStartTime;

            using (var conn = TempSqliteUtils.GetTempConnection())
            {
                conn.Open();

                var trainCols = GetColumns(conn, "training_set");
                var testCols = GetColumns(conn, "testing_set");
                if (trainCols.Count == 0 || testCols.Count == 0)
                {
                    throw new InvalidOperationException("临时库里找不到 training_set / testing_set 表");
                }

                // 只用两边都有的列（历史版本里 testing_set 可能少 FolderName / FolderPath）
                var cols = trainCols.Where(c => testCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                if (cols.Count == 0)
                {
                    throw new InvalidOperationException("training_set 与 testing_set 没有可共用的列");
                }
                string colList = string.Join(", ", cols.Select(c => "[" + c + "]"));
                string sourceCols = string.Join(", ", cols.Select(c => "s.[" + c + "]"));

                using (var tx = conn.BeginTransaction())
                {
                    conn.Execute("DELETE FROM [training_set]", transaction: tx);
                    conn.Execute("DELETE FROM [testing_set]", transaction: tx);

                    conn.Execute(
                        $@"INSERT INTO [testing_set] ({colList})
                           SELECT {sourceCols} FROM [{SourceTable}] s
                           WHERE s.Id IN (SELECT Id FROM [{FoldMapTable}] WHERE FoldIndex = @f)",
                        new { f = foldIndex }, tx);

                    conn.Execute(
                        $@"INSERT INTO [training_set] ({colList})
                           SELECT {sourceCols} FROM [{SourceTable}] s
                           WHERE s.Id NOT IN (SELECT Id FROM [{FoldMapTable}] WHERE FoldIndex = @f)",
                        new { f = foldIndex }, tx);

                    testCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM [testing_set]", transaction: tx);
                    trainCount = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM [training_set]", transaction: tx);
                    tx.Commit();
                }
            }
        }

        /// <summary>
        /// 日志用的一行耗时提示：本折用时 / 已跑几次 / 累计 / 平均每折 / 预计还需。
        /// done = 已经跑完的次数，total = 一共要跑多少次（折数 × 重复轮数）。
        /// </summary>
        public static string FoldTimingText(int done, int total)
        {
            try
            {
                if (_foldStartTime == DateTime.MinValue) return "";
                double foldMin = (DateTime.Now - _foldStartTime).TotalMinutes;
                double totalMin = _totalStartTime == DateTime.MinValue
                    ? foldMin : (DateTime.Now - _totalStartTime).TotalMinutes;
                double avg = done > 0 ? totalMin / done : foldMin;
                int left = total - done;
                double remain = left > 0 ? avg * left : 0;
                return string.Format(CultureInfo.InvariantCulture,
                    "本折用时 {0:F1} 分钟 ｜ 已跑 {1}/{2} 次，累计 {3:F1} 分钟，平均每折 {4:F1} 分钟，预计还需约 {5:F1} 分钟",
                    foldMin, done, total, totalMin, avg, remain);
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>交叉验证跑完（或取消）后，把全量数据还原成 training_set（清空 testing_set），让用户能继续正常训练/预测</summary>
        public static void RestoreAllToTraining()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    bool hasSource = conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n", new { n = SourceTable }) > 0;
                    if (!hasSource) return;

                    var trainCols = GetColumns(conn, "training_set");
                    var srcCols = GetColumns(conn, SourceTable);
                    var cols = trainCols.Where(c => srcCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (cols.Count == 0) return;
                    string colList = string.Join(", ", cols.Select(c => "[" + c + "]"));
                    string sourceCols = string.Join(", ", cols.Select(c => "s.[" + c + "]"));

                    using (var tx = conn.BeginTransaction())
                    {
                        conn.Execute("DELETE FROM [testing_set]", transaction: tx);
                        conn.Execute("DELETE FROM [training_set]", transaction: tx);
                        conn.Execute($"INSERT INTO [training_set] ({colList}) SELECT {sourceCols} FROM [{SourceTable}] s ORDER BY s.Id", transaction: tx);
                        tx.Commit();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static List<string> GetColumns(SqliteConnection conn, string table)
        {
            try
            {
                return conn.Query<string>(
                    "SELECT name FROM pragma_table_info(@t)", new { t = table }).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>解析内核写出的逐折报告（指标在末尾三行，格式见内核源码）</summary>
        public static FoldMetric ParseReport(string reportPath, int repeat, int fold, int trainCount, int testCount)
        {
            var m = new FoldMetric
            {
                Repeat = repeat,
                Fold = fold,
                TrainCount = trainCount,
                TestCount = testCount
            };

            try
            {
                if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
                {
                    m.Message = "没有生成报告文件";
                    return m;
                }

                string text = File.ReadAllText(reportPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    m.Message = "报告文件是空的";
                    return m;
                }

                double a, mf, wf;
                bool okA = TryPick(text, @"整体准确率accuracy:\s*([0-9]*\.?[0-9]+)", out a);
                bool okM = TryPick(text, @"Macro F1 Score:\s*([0-9]*\.?[0-9]+)", out mf);
                bool okW = TryPick(text, @"Weighted F1 Score:\s*([0-9]*\.?[0-9]+)", out wf);

                if (!okA && !okM && !okW)
                {
                    m.Message = "报告里没有指标行（可能评估阶段出错）";
                    return m;
                }

                m.Accuracy = okA ? a : double.NaN;
                m.MacroF1 = okM ? mf : double.NaN;
                m.WeightedF1 = okW ? wf : double.NaN;
                m.Classes = ParseClassRows(text);
                m.Parsed = true;
                return m;
            }
            catch (Exception ex)
            {
                m.Message = ex.Message;
                return m;
            }
        }

        private static bool TryPick(string text, string pattern, out double value)
        {
            value = double.NaN;
            var match = Regex.Match(text, pattern);
            if (!match.Success) return false;
            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryNum(string s, out double v)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>
        /// 从报告里抠出"逐类指标"那些行。内核的报告是 sklearn classification_report 的格式：
        ///     类别名  precision  recall  f1-score  support
        /// 类别名里可能带空格，所以**从右往左**取最后 4 个数字，剩下的都算类别名。
        /// </summary>
        public static List<ClassRow> ParseClassRows(string text)
        {
            var list = new List<ClassRow>();
            if (string.IsNullOrEmpty(text)) return list;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Contains("precision") && line.Contains("support")) continue;   // 表头
                if (line.Contains("accuracy")) continue;                                // accuracy 行
                if (line.Contains("avg")) continue;                                    // macro/weighted/micro avg 行

                var t = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 5) continue;

                double p, r, f, s;
                if (!TryNum(t[t.Length - 4], out p)) continue;
                if (!TryNum(t[t.Length - 3], out r)) continue;
                if (!TryNum(t[t.Length - 2], out f)) continue;
                if (!TryNum(t[t.Length - 1], out s)) continue;

                string label = string.Join(" ", t, 0, t.Length - 4).Trim();
                if (label.Length == 0) continue;
                if (label.StartsWith("整体") || label.StartsWith("Macro") || label.StartsWith("Weighted")) continue;

                list.Add(new ClassRow
                {
                    Label = label,
                    Precision = p,
                    Recall = r,
                    F1 = f,
                    Support = (long)Math.Round(s),
                    FoldCount = 1
                });
            }
            return list;
        }

        /// <summary>
        /// 把各折的逐类指标按类别求平均（说明书要求"记录准确率、召回率等指标并对 K 次结果取平均"）。
        /// 只在"该折考卷里出现过这个类别"的折上平均；support 是各折求和（折互不重叠 → = 该类总条数）。
        /// </summary>
        public static List<ClassRow> AverageClasses(IEnumerable<FoldMetric> metrics)
        {
            var acc = new Dictionary<string, ClassRow>();
            var hits = new Dictionary<string, int>();
            var order = new List<string>();

            foreach (var m in metrics)
            {
                if (m == null || m.Classes == null) continue;
                foreach (var c in m.Classes)
                {
                    if (!acc.ContainsKey(c.Label))
                    {
                        acc[c.Label] = new ClassRow { Label = c.Label };
                        hits[c.Label] = 0;
                        order.Add(c.Label);
                    }
                    var a = acc[c.Label];
                    a.Precision = AddNum(a.Precision, c.Precision);
                    a.Recall = AddNum(a.Recall, c.Recall);
                    a.F1 = AddNum(a.F1, c.F1);
                    a.Support += c.Support;
                    hits[c.Label] = hits[c.Label] + 1;
                }
            }

            var result = new List<ClassRow>();
            foreach (var label in order)
            {
                var a = acc[label];
                int n = hits[label];
                if (n > 0)
                {
                    a.Precision = a.Precision / n;
                    a.Recall = a.Recall / n;
                    a.F1 = a.F1 / n;
                }
                a.FoldCount = n;
                result.Add(a);
            }
            return result;
        }

        private static double AddNum(double acc, double v)
        {
            if (double.IsNaN(v)) return acc;
            return double.IsNaN(acc) ? v : acc + v;
        }

        /// <summary>
        /// 渲染逐类指标表（客户端结果页的下半部分就是按这个格式解析的：
        /// 每行"类别名 + 4 个数字"，否则会被忽略）。
        /// </summary>
        public static string RenderClassTable(List<ClassRow> rows)
        {
            var sb = new StringBuilder();
            if (rows == null || rows.Count == 0) return sb.ToString();
            sb.AppendLine("precision  recall  f1-score  support");
            foreach (var c in rows)
            {
                sb.AppendLine(c.Label + "  " + N4(c.Precision) + "  " + N4(c.Recall) + "  "
                            + N4(c.F1) + "  " + c.Support.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string N4(double v)
        {
            if (double.IsNaN(v)) return "0.0000";
            return v.ToString("F4", CultureInfo.InvariantCulture);
        }

        /// <summary>平均 ± 波动（总体标准差；自动跳过没有值的折）</summary>
        public static MetricSummary Summarize(IEnumerable<double> values)
        {
            var list = values.Where(v => !double.IsNaN(v)).ToList();
            var s = new MetricSummary();
            if (list.Count == 0) return s;

            s.Count = list.Count;
            s.Mean = list.Average();
            s.Min = list.Min();
            s.Max = list.Max();
            double sum = list.Sum(v => (v - s.Mean) * (v - s.Mean));
            s.Std = Math.Sqrt(sum / list.Count);
            return s;
        }

        /// <param name="looPooled">留一专用：把全部折外预测汇总成的那一份指标（null = 不用汇总口径）</param>
        /// <param name="looPooledSamples">参与汇总的折外预测条数</param>
        public static CrossValidationSummary BuildSummary(string methodName, int foldCount, int repeatCount,
            int sampleCount, List<string> knownLabels, List<string> singleSampleLabels, List<FoldMetric> metrics,
            FoldMetric looPooled = null, int looPooledSamples = 0)
        {
            var summary = new CrossValidationSummary
            {
                MethodName = methodName,
                FoldCount = foldCount,
                RepeatCount = repeatCount,
                TotalRuns = metrics.Count,
                FailedRuns = metrics.Count(x => !x.Parsed),
                SampleCount = sampleCount,
                KnownLabels = knownLabels ?? new List<string>(),
                SingleSampleLabels = singleSampleLabels ?? new List<string>(),
                Metrics = metrics ?? new List<FoldMetric>()
            };
            if (looPooled != null && looPooled.Parsed)
            {
                //留一：每折只有 1 条考卷，"各折平均"会把"只是被预测成这一类、考卷里并没有这一类"的折也算进分母
                //（2026-09-30 实测：24 条数据 12 正 12 负，正面召回率被算成 11/24=0.4583，实际是 11/12=0.9167）
                //→ 改成把全部折外预测汇总成一张混淆矩阵再算（教科书留一口径）
                summary.PooledLoo = true;
                summary.PooledSampleCount = looPooledSamples > 0 ? looPooledSamples : looPooled.TestCount;
                summary.Accuracy = PooledMetric(looPooled.Accuracy, summary.PooledSampleCount);
                summary.MacroF1 = PooledMetric(looPooled.MacroF1, summary.PooledSampleCount);
                summary.WeightedF1 = PooledMetric(looPooled.WeightedF1, summary.PooledSampleCount);
                summary.ClassAverages = looPooled.Classes;
            }
            else
            {
                summary.Accuracy = Summarize(summary.Metrics.Select(x => x.Accuracy));
                summary.MacroF1 = Summarize(summary.Metrics.Select(x => x.MacroF1));
                summary.WeightedF1 = Summarize(summary.Metrics.Select(x => x.WeightedF1));
                summary.ClassAverages = AverageClasses(summary.Metrics);
            }
            return summary;
        }

        /// <summary>留一：单张混淆矩阵汇总出来的值（没有"波动"这一说，写清参与汇总的条数即可）</summary>
        private static MetricSummary PooledMetric(double v, int samples)
        {
            return new MetricSummary { Count = 1, Mean = v, Std = 0, Min = v, Max = v, Pooled = true, PooledSamples = samples };
        }

        //==================== 留一专用：借 dev_set 取「逐条预测」 ====================
        // 为什么需要：留一每折只剩 1 条考卷，而内核用 sklearn 的 classification_report(target_names=...)
        // 出报告 —— 测试集只覆盖 1 个类别时会抛 Number of classes 不匹配、内核直接不写报告，
        // 于是"只有模型猜错的折才有指标"。内核在**应用模型**模式（--isUploadModel 1）下会把每条的
        // 预测写回 dev_set.PreLabel（LargeModelDemoText.py:382），所以留一走这条路：
        // 每折训练完 → 客户端再调一次内核（应用模型）→ 读 dev_set.PreLabel → 客户端自己算指标。

        private const string DevBackupTable = "cv_dev_backup";

        /// <summary>备份当前 dev_set（交叉验证期间借它取逐条预测，结束/中断时还原）</summary>
        public static void SnapshotDevSet()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    bool hasDev = conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='dev_set'") > 0;
                    if (!hasDev) return;
                    using (var tx = conn.BeginTransaction())
                    {
                        conn.Execute($"DROP TABLE IF EXISTS [{DevBackupTable}]", transaction: tx);
                        conn.Execute($"CREATE TABLE [{DevBackupTable}] AS SELECT * FROM [dev_set]", transaction: tx);
                        tx.Commit();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>还原 dev_set（并丢掉备份表）——正常结束、取消、出错都要调</summary>
        public static void RestoreDevSet()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    bool hasBak = conn.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n",
                        new { n = DevBackupTable }) > 0;
                    if (!hasBak) return;

                    var devCols = GetColumns(conn, "dev_set");
                    var bakCols = GetColumns(conn, DevBackupTable);
                    var cols = devCols.Where(c => bakCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

                    using (var tx = conn.BeginTransaction())
                    {
                        if (cols.Count > 0)
                        {
                            string colList = string.Join(", ", cols.Select(c => "[" + c + "]"));
                            conn.Execute("DELETE FROM [dev_set]", transaction: tx);
                            conn.Execute($"INSERT INTO [dev_set] ({colList}) SELECT {colList} FROM [{DevBackupTable}]", transaction: tx);
                        }
                        conn.Execute($"DROP TABLE IF EXISTS [{DevBackupTable}]", transaction: tx);
                        tx.Commit();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把本折的 testing_set 复制到 dev_set（只留本折那几条），返回 dev_set 条数</summary>
        public static int CopyTestingToDevSet()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    var devCols = GetColumns(conn, "dev_set");
                    var testCols = GetColumns(conn, "testing_set");
                    var cols = devCols.Where(c => testCols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (cols.Count == 0) return 0;

                    string colList = string.Join(", ", cols.Select(c => "[" + c + "]"));
                    using (var tx = conn.BeginTransaction())
                    {
                        conn.Execute("DELETE FROM [dev_set]", transaction: tx);
                        conn.Execute($"INSERT INTO [dev_set] ({colList}) SELECT {colList} FROM [testing_set]", transaction: tx);
                        tx.Commit();
                    }
                    return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM [dev_set]");
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>清空 dev_set（留一跑到"最终模型"那一次时用：别把上一折的考卷再预测一遍）</summary>
        public static void ClearDevSet()
        {
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    conn.Execute("DELETE FROM [dev_set]");
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 读回 dev_set 的逐条预测（id → 内核写回的 PreLabel）。用**原生 reader**读，不走 Dapper 动态映射，
        /// 免得关键字/别名踩坑；读不到就把原因写进 error（留一的诊断全靠它）。
        /// </summary>
        public static Dictionary<long, string> ReadDevPredictions(out string error)
        {
            error = "";
            var d = new Dictionary<long, string>();
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, IFNULL(PreLabel,''), IFNULL(PreTime,'') FROM [dev_set]";
                        using (var rd = cmd.ExecuteReader())
                        {
                            int rows = 0, filled = 0;
                            while (rd.Read())
                            {
                                rows++;
                                long id = rd.GetInt64(0);
                                string p = rd.IsDBNull(1) ? "" : rd.GetString(1);
                                if (!string.IsNullOrWhiteSpace(p)) { d[id] = p.Trim(); filled++; }
                            }
                            error = "dev_set 共 " + rows + " 行，其中 " + filled + " 行有 PreLabel";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读 dev_set 出错：" + ex.Message;
            }
            return d;
        }

        /// <summary>
        /// 读某张表里的 id → 真实标签（留一比对用；table 传 cv_source 或 testing_set）。
        /// 同样用原生 reader；读不到就把原因写进 error。
        /// </summary>
        public static Dictionary<long, string> ReadTruthLabels(string table, out string error)
        {
            error = "";
            var d = new Dictionary<long, string>();
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, IFNULL(TextLabelName,'') FROM [" + table + "]";
                        using (var rd = cmd.ExecuteReader())
                        {
                            int rows = 0, filled = 0;
                            while (rd.Read())
                            {
                                rows++;
                                long id = rd.GetInt64(0);
                                string lab = rd.IsDBNull(1) ? "" : rd.GetString(1);
                                d[id] = (lab ?? "").Trim();
                                if (!string.IsNullOrWhiteSpace(lab)) filled++;
                            }
                            error = table + " 共 " + rows + " 行，其中 " + filled + " 行有标签";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "读 " + table + " 出错：" + ex.Message;
            }
            return d;
        }

        /// <summary>
        /// 用「逐条预测」拼出某一折的指标（留一专用）。逐类 precision/recall/f1 按这一折的混淆矩阵算，
        /// macro / weighted F1 与 sklearn 口径一致；support = 该类在真实标签里的条数。
        /// </summary>
        public static FoldMetric BuildFoldMetricFromPredictions(int repeat, int fold, int trainCount, int testCount,
            Dictionary<long, string> truth, Dictionary<long, string> pred)
        {
            var m = new FoldMetric { Repeat = repeat, Fold = fold, TrainCount = trainCount, TestCount = testCount };

            var pairs = new List<KeyValuePair<string, string>>();     // Key = 真实标签，Value = 预测标签
            if (pred != null)
            {
                foreach (var kv in pred)
                {
                    string t;
                    if (truth == null || !truth.TryGetValue(kv.Key, out t)) continue;
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    pairs.Add(new KeyValuePair<string, string>(t.Trim(), kv.Value));
                }
            }
            if (pairs.Count == 0)
            {
                m.Message = "预测与真实标签配不上（预测 " + (pred == null ? 0 : pred.Count)
                          + " 条 / 真实标签 " + (truth == null ? 0 : truth.Count) + " 条）";
                return m;
            }

            int total = pairs.Count;
            int correct = pairs.Count(x => x.Key == x.Value);
            var labels = pairs.Select(x => x.Key).Concat(pairs.Select(x => x.Value))
                              .Where(x => !string.IsNullOrWhiteSpace(x))
                              .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            var rows = new List<ClassRow>();
            foreach (var lab in labels)
            {
                int tp = pairs.Count(x => x.Value == lab && x.Key == lab);
                int fp = pairs.Count(x => x.Value == lab && x.Key != lab);
                int fn = pairs.Count(x => x.Key == lab && x.Value != lab);
                long support = pairs.Count(x => x.Key == lab);
                if (support == 0 && tp + fp == 0) continue;        // 这一折跟它无关 → 不参与该类平均

                double f1 = 0;
                if (2 * tp + fp + fn > 0) f1 = (2.0 * tp) / (2.0 * tp + fp + fn);

                rows.Add(new ClassRow
                {
                    Label = lab,
                    Precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0,
                    Recall = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0,
                    F1 = f1,
                    Support = support,
                    FoldCount = 1
                });
            }

            m.Classes = rows;
            m.Accuracy = (double)correct / total;
            m.MacroF1 = rows.Count > 0 ? rows.Average(x => x.F1) : double.NaN;
            double wsum = rows.Sum(x => x.F1 * x.Support);
            m.WeightedF1 = total > 0 ? wsum / total : double.NaN;
            m.Parsed = true;
            return m;
        }

        /// <summary>按 TextContent 读 dev_set 的逐条预测（id 万一被重编号时的兜底；用原生 reader）</summary>
        public static Dictionary<string, string> ReadDevPredictionsByText(out string error)
        {
            error = "";
            var d = new Dictionary<string, string>();
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT IFNULL(TextContent,''), IFNULL(PreLabel,'') FROM [dev_set]";
                        using (var rd = cmd.ExecuteReader())
                        {
                            int rows = 0, filled = 0;
                            while (rd.Read())
                            {
                                rows++;
                                string text = rd.IsDBNull(0) ? "" : rd.GetString(0);
                                string p = rd.IsDBNull(1) ? "" : rd.GetString(1);
                                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(p)) continue;
                                d[text.Trim()] = p.Trim();
                                filled++;
                            }
                            error = "dev_set 按文本读：" + rows + " 行 / 有效 " + filled + " 行";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "按文本读 dev_set 出错：" + ex.Message;
            }
            return d;
        }

        /// <summary>按 TextContent 读某张表的真实标签（兜底用）</summary>
        public static Dictionary<string, string> ReadTruthLabelsByText(string table, out string error)
        {
            error = "";
            var d = new Dictionary<string, string>();
            try
            {
                using (var conn = TempSqliteUtils.GetTempConnection())
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT IFNULL(TextContent,''), IFNULL(TextLabelName,'') FROM [" + table + "]";
                        using (var rd = cmd.ExecuteReader())
                        {
                            int rows = 0, filled = 0;
                            while (rd.Read())
                            {
                                rows++;
                                string text = rd.IsDBNull(0) ? "" : rd.GetString(0);
                                string lab = rd.IsDBNull(1) ? "" : rd.GetString(1);
                                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(lab)) continue;
                                d[text.Trim()] = lab.Trim();
                                filled++;
                            }
                            error = table + " 按文本读：" + rows + " 行 / 有效 " + filled + " 行";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = "按文本读 " + table + " 出错：" + ex.Message;
            }
            return d;
        }

        /// <summary>
        /// 留一：把"本折的逐条预测"和"本折的真实标签"配对后算出指标。
        /// 先按 id 配（正常情况）；配不上就按 TextContent 配（万一 dev_set 的 id 被重编号）。
        /// diag 里会写清两种读法各读到多少、报什么错、配对结果与逐条明细（直接打进日志，方便定位）。
        /// </summary>
        public static FoldMetric BuildLooFoldMetric(int repeat, int fold, int trainCount, int testCount, out string diag,
            out List<KeyValuePair<string, string>> foldPairs)
        {
            string predErrId = "", truthErrId = "";
            var predById = ReadDevPredictions(out predErrId);
            var truthById = ReadTruthLabels("testing_set", out truthErrId);

            string predErrText = "", truthErrText = "";
            var predByText = ReadDevPredictionsByText(out predErrText);
            var truthByText = ReadTruthLabelsByText("testing_set", out truthErrText);

            var pairs = new List<KeyValuePair<string, string>>();      // Key = 真实标签，Value = 预测
            string how = "";

            if (predById.Count > 0 && truthById.Count > 0)
            {
                foreach (var kv in predById)
                {
                    string t;
                    if (!truthById.TryGetValue(kv.Key, out t) || string.IsNullOrWhiteSpace(t)) continue;
                    pairs.Add(new KeyValuePair<string, string>(t.Trim(), kv.Value));
                }
                if (pairs.Count > 0) how = "按 id 配对";
            }
            if (pairs.Count == 0 && predByText.Count > 0 && truthByText.Count > 0)
            {
                foreach (var kv in predByText)
                {
                    string t;
                    if (!truthByText.TryGetValue(kv.Key, out t) || string.IsNullOrWhiteSpace(t)) continue;
                    pairs.Add(new KeyValuePair<string, string>(t.Trim(), kv.Value));
                }
                if (pairs.Count > 0) how = "按文本配对（id 对不上时的兜底）";
            }

            var m = BuildMetricFromPairs(repeat, fold, trainCount, testCount, pairs);

            var sb = new StringBuilder();
            sb.Append("逐条预测 ").Append(predById.Count).Append(" 条（").Append(predErrId).Append("）；本折标签 ")
              .Append(truthById.Count).Append(" 条（").Append(truthErrId).Append("）");
            if (pairs.Count == 0)
            {
                sb.Append("；兜底读法：").Append(predErrText).Append(" / ").Append(truthErrText);
            }
            else
            {
                sb.Append("；").Append(how).Append(" ").Append(pairs.Count).Append(" 条");
            }
            if (pairs.Count > 0)
            {
                sb.Append("；明细：");
                foreach (var p in pairs)
                {
                    sb.Append("实际 ").Append(p.Key).Append("→预测 ").Append(p.Value)
                      .Append(p.Key == p.Value ? " ✔；" : " ✘；");
                }
            }
            diag = sb.ToString();

            if (!m.Parsed)
            {
                m.Message = "逐条预测与真实标签配不上（预测 " + predById.Count + " 条 / 标签 " + truthById.Count
                          + " 条；文本兜底 " + predByText.Count + " / " + truthByText.Count + "）";
            }
            foldPairs = pairs;      //留一专用：把这些折外预测交回调用方累计，最后汇总成一张混淆矩阵
            return m;
        }

        private static FoldMetric BuildMetricFromPairs(int repeat, int fold, int trainCount, int testCount,
            List<KeyValuePair<string, string>> pairs)
        {
            var m = new FoldMetric { Repeat = repeat, Fold = fold, TrainCount = trainCount, TestCount = testCount };
            if (pairs == null || pairs.Count == 0) return m;

            int total = pairs.Count;
            int correct = pairs.Count(x => x.Key == x.Value);
            var labels = pairs.Select(x => x.Key).Concat(pairs.Select(x => x.Value))
                              .Where(x => !string.IsNullOrWhiteSpace(x))
                              .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

            var rows = new List<ClassRow>();
            foreach (var lab in labels)
            {
                int tp = pairs.Count(x => x.Value == lab && x.Key == lab);
                int fp = pairs.Count(x => x.Value == lab && x.Key != lab);
                int fn = pairs.Count(x => x.Key == lab && x.Value != lab);
                long support = pairs.Count(x => x.Key == lab);
                if (support == 0 && tp + fp == 0) continue;

                double f1 = 0;
                if (2 * tp + fp + fn > 0) f1 = (2.0 * tp) / (2.0 * tp + fp + fn);

                rows.Add(new ClassRow
                {
                    Label = lab,
                    Precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0,
                    Recall = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0,
                    F1 = f1,
                    Support = support,
                    FoldCount = 1
                });
            }

            m.Classes = rows;
            m.Accuracy = (double)correct / total;
            m.MacroF1 = rows.Count > 0 ? rows.Average(x => x.F1) : double.NaN;
            double wsum = rows.Sum(x => x.F1 * x.Support);
            m.WeightedF1 = total > 0 ? wsum / total : double.NaN;
            m.Parsed = true;
            return m;
        }

        /// <summary>
        /// 留一专用：把全部折（24 条数据 = 24 折）的折外预测汇总成一份指标 —— 教科书留一口径。
        /// 为什么不能按"各折平均"算：留一每折只剩 1 条考卷，某一折的"正面"这一格，在"考卷其实是负面、
        /// 但模型答成了正面"时也会被记一个 0，于是正面的分母被撑到 24（真正的正面只有 12 条）→ 指标被稀释一半。
        /// </summary>
        public static FoldMetric BuildLooPooledMetric(List<KeyValuePair<string, string>> allPairs)
        {
            var pairs = allPairs ?? new List<KeyValuePair<string, string>>();
            var m = BuildMetricFromPairs(0, 0, 0, pairs.Count, pairs);
            if (m.Parsed)
            {
                foreach (var c in m.Classes) c.FoldCount = 1;   //汇总口径下没有"参与平均的折数"
            }
            return m;
        }

        /// <summary>删掉某一折用完的模型目录：每折 400+MB，指标取完就没用了（十折×10轮能写出 45GB）</summary>
        public static void DeleteFoldModelDir(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception)
            {
                //删不掉也绝不打断交叉验证（下一轮开始时会再清一次）
            }
        }

        /// <summary>
        /// 每轮交叉验证开始前清掉上一轮留下的折模型与折报告：
        ///   · 折模型留着只会撑爆磁盘（每折 400+MB）
        ///   · 折报告——内核"考卷只覆盖 1 个类别"时不写报告，旧文件会留在目录里被误当成这一轮的证据（2026-09-30 实测踩到）
        /// </summary>
        public static void CleanPreviousFoldArtifacts(string cvDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cvDir) || !Directory.Exists(cvDir)) return;
                foreach (string dir in Directory.GetDirectories(cvDir, "model_run*"))
                {
                    try { Directory.Delete(dir, true); } catch (Exception) { }
                }
                foreach (string file in Directory.GetFiles(cvDir, "result_run*_fold*.txt"))
                {
                    try { File.Delete(file); } catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
