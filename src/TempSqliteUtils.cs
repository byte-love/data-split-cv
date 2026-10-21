using DemoTextClient.Models.BigModelModels;
using Dapper;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoTextClient.Utils.SqliteConnectUtils
{
    public class TempSqliteUtils
    {

        public static SqliteConnection GetTempConnection()
        {
            string pyexePath = AppDomain.CurrentDomain.BaseDirectory + @"Resources\BigModel\SqliteData_Temp.sqlite";
            var connStr = @"Data Source=" + pyexePath + ";";
            return new SqliteConnection(connStr);
        }

        //根据表名称删除所有该表数据
        public static void clearTableData(String _tableName)
        {

            try
            {
                using var connection = GetTempConnection();
                connection.Open();
                string deleteSql = @$"DELETE FROM [{_tableName}]";
                connection.Execute(deleteSql);
            }
            catch (Exception ex)
            {

            }
        }
        //获取训练集 或 测试集的 总条数
        public static int GetTotalCount(string tableName)
        {
            try
            {
                using var connection = GetTempConnection();
                connection.Open();

                // 防止SQL注入，使用参数化查询
                string sql = $"SELECT COUNT(*) FROM [{tableName}]";

                int totalCount = connection.ExecuteScalar<int>(sql);
                return totalCount;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取表{tableName}总条数失败: {ex.Message}");
                return 0; // 或者抛出自定义异常
            }
        }
        public static int TransferDataByRatioWithFilterTemp(double testRatio,double trainingRatio)
        {
            

            using (var connection = GetTempConnection())
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        // 1. 获取符合条件的训练集数量（使用参数化查询防止SQL注入）
                        string countSql = @"SELECT COUNT(*) FROM training_set";
                        int totalCount = connection.ExecuteScalar<int>(
                            countSql,transaction: transaction);

                        if (totalCount == 0)
                            return 0;

                        // 2. 计算需要转移的数量
                        double transferRatio = testRatio / (testRatio + trainingRatio);
                        int transferCount = (int)Math.Round(totalCount * transferRatio);

                        if (transferCount <= 0)
                            return 0;

                        // 3. 随机选择要转移的数据（获取ID）
                        string selectSql = @"
                    SELECT * FROM training_set 
                    ORDER BY RANDOM() 
                    LIMIT @TransferCount";

                        var recordsToTransfer = connection.Query<TrainTempModels>(
                            selectSql,
                            new {  TransferCount = transferCount },
                            transaction: transaction).ToList();

                        if (!recordsToTransfer.Any())
                            return 0;

                        // 4. 插入到测试集
                        const string insertSql = @"
                    INSERT INTO testing_set 
                    (FilePath, TextContent, TextLabelName, CreateTime, 
                     Model, FileSize, FileType, Status, FileName) 
                    VALUES (@FilePath, @TextContent, @TextLabelName, @CreateTime, 
                            @Model, @FileSize, @FileType, @Status, @FileName)";

                        int insertedCount = connection.Execute(
                            insertSql,
                            recordsToTransfer,
                            transaction: transaction);

                        // 5. 从训练集删除（根据ID字段）
                        var idsToDelete = recordsToTransfer.Select(r => r.id).ToList();
                        const string deleteSql = @"
                    DELETE FROM training_set 
                    WHERE Id IN @Ids";

                        int deletedCount = connection.Execute(
                            deleteSql,
                            new { Ids = idsToDelete },
                            transaction: transaction);

                        transaction.Commit();

                        Console.WriteLine($"转移完成：从 {totalCount} 条记录中转移了 {insertedCount} 条");
                        return insertedCount;
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        Console.WriteLine($"数据转移失败：{ex.Message}");
                        throw;
                    }
                }
            }
        }


        //插入训练集数据 
        public static void InserTraintOrTest(List<TrainTempModels> trainModels,String _TableName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_TableName))
                {
                    _TableName = "training_set";

                }
                // _TableName  
                //    training_set
                //    testing_set

                string sql = @$"INSERT INTO [{_TableName}] 
                (TextContent,  TextLabelName,CreateTime, Model, FileSize,FileType,Status,FileName,FilePath,FolderName,FolderPath) 
                VALUES (@TextContent, @TextLabelName,@CreateTime ,@Model,@FileSize,@FileType,@Status,@FileName,@FilePath,@FolderName,@FolderPath)";
                try
                {
                    using (SqliteConnection connection = GetTempConnection())
                    {
                        if (connection.State == ConnectionState.Closed)
                        {
                            connection.Open();
                        }

                        using var transaction = connection.BeginTransaction();
                        try
                        {
                            var result = connection.Execute(sql, trainModels, transaction);
                            transaction.Commit();
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }

                    }

                }
                catch (Exception ex)
                {
                }
            }
            catch (Exception ex)
            {

            }
        }


        //插入预测集数据
        public static void InserDev(List<DevTempModels> devModels)
        {
            try
            {
                const string sql = @"INSERT INTO [dev_set] 
    (Status, TextContent, CreateTime, ModelId, FileSize, FileType, 
     PreLabel, ModelType, PreTime, FilePath, FileName, FolderName, FolderPath,Id) 
    VALUES 
    ( @Status, @TextContent, @CreateTime, @ModelId, @FileSize, @FileType, 
     @PreLabel, @ModelType, @PreTime, @FilePath, @FileName, @FolderName, @FolderPath,@Id)";
                try
                {
                    using (SqliteConnection connection = GetTempConnection())
                    {
                        if (connection.State == ConnectionState.Closed)
                        {
                            connection.Open();
                        }

                        using var transaction = connection.BeginTransaction();
                        try
                        {
                            var result = connection.Execute(sql, devModels, transaction);
                            transaction.Commit();
                        }
                        catch
                        {
                            transaction.Rollback();
                            throw;
                        }

                    }

                }
                catch (Exception ex)
                {
                }
            }
            catch (Exception ex)
            {

            }
        }




        public static void selectAndUpateDev(List<DevTempModels> devModels,String sqliteFilePath)
        {
            try
            {

              
                using var connection = GetTempConnection();
                connection.Open();

                const string sql = @"SELECT * FROM [dev_set] 
                     WHERE PreLabel IS NOT NULL 
                     AND PreLabel != '' 
                     AND LENGTH(TRIM(PreLabel)) > 0";
                var demodl = connection.Query<DevTempModels>(sql);
             
                foreach (var record in demodl)
                {
                    
                }
            }
            catch (Exception ex)
            {
            }
        }


    }
}
