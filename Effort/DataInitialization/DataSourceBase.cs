﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Reflection;
using MMDB.Table;
using MMDB.EntityFrameworkProvider.Helpers;

namespace MMDB.EntityFrameworkProvider.DataInitialization
{
    public abstract class DataSourceBase : IDataSource
    {
        private string[] propertyNames;
        private Type[] propertyTypes;
        private Delegate initializer;

        public DataSourceBase(Type entityType)
        {
            PropertyInfo[] properties = entityType.GetProperties();

            this.propertyNames = properties.Select(p => p.Name).ToArray();
            this.propertyTypes = properties.Select(p => p.PropertyType).ToArray();

            this.initializer = LambdaExpressionHelper.CreateInitializerExpression(entityType, properties).Compile();
        }

        public virtual IEnumerable<object> GetInitialRecords()
        {
            int?[] mapper = new int?[propertyNames.Length];
            object[] propertyValues = new object[propertyNames.Length];

            using (IDataReader reader = this.CreateDataReader())
            {
                // Setup field order mapper
                for (int i = 0; i < this.propertyNames.Length; i++)
                {
                    // Find the index of the field in the datareader
                    for (int j = 0; j < reader.FieldCount; j++)
                    {
                        if (string.Equals(this.propertyNames[i], reader.GetName(j), StringComparison.InvariantCultureIgnoreCase))
                        {
                            mapper[i] = j;
                            break;
                        }
                    }
                }
                while (reader.Read())
                {
                    for (int i = 0; i < this.propertyNames.Length; i++)
                    {
                        // Get the index of the field (in the DataReader)
                        int? fieldIndex = mapper[i];

                        if (!fieldIndex.HasValue)
                        {
                            continue;
                        }

                        object fieldValue = reader.GetValue(fieldIndex.Value);

                        propertyValues[i] = this.ConvertValue(fieldValue, this.propertyTypes[i]);
                    }

                    object entity = this.initializer.DynamicInvoke(propertyValues);

                    yield return entity;
                }
            }
        }


        protected abstract IDataReader CreateDataReader();

        protected virtual object ConvertValue(object value, Type type)
        {
            return value;
        }

    }
}
