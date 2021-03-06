﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Runtime.CompilerServices;

namespace SecurityDriven.TinyORM.Extensions
{
	using Utils;

	using DbString = Tuple<string, StringType>;

	internal static class CommandExtensions
	{
		static SqlParameter GenerateParameter(string parameterName, object data)
		{
			var p = new SqlParameter() { ParameterName = parameterName };
			var stringType = StringType.NVARCHAR;

			if (data is ValueType dataValue)
			{
				// if data is DateTime, switch to higher precision of DateTime2
				if (dataValue is DateTime dataDateTime)
				{
					p.DbType = DbType.DateTime2;
					p.Value = data;
					return p;
				}
			}
			else
			{
				// check if data is DbString
				if (data is DbString dbString)
				{
					data = dbString.Item1;
					stringType = dbString.Item2;
					p.DbType = (DbType)stringType;
				}

				// check if data is regular string
				if (data is string dataString)
				{
					int lenThreshold = (stringType == StringType.VARCHAR || stringType == StringType.CHAR) ? MAX_ANSI_STRING_LENGTH : MAX_UNICODE_STRING_LENGTH;
					p.Size = dataString.Length > lenThreshold ? -1 : lenThreshold;
					p.Value = data;
					return p;
				}

				// check if data is NULL
				if (data is Type dataType)
				{
					var dbType = typeMap[dataType];
					p.DbType = dbType;
					if (dbType == DbType.Binary) p.Size = -1;
					p.Value = DBNull.Value;
					return p;
				}

				if (data is DataTable dataTable)
				{
					p.SqlDbType = SqlDbType.Structured;
					p.TypeName = dataTable.TableName;
					p.Value = dataTable;
					return p;
				}
			}// data is not a struct
			p.Value = data;
			return p;
		}// GenerateParameter()

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void SetParameters(this SqlCommand command, object obj, Type objType)
		{
			var objPropertyValueDictionary = ObjectFactory.ObjectToDictionary(obj, objType, parameterize: true);
			SetParameters(command, objPropertyValueDictionary);
		}// SetParameters()

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		static void SetParameters<T>(this SqlCommand command, T obj) where T : class
		{
			var objPropertyValueDictionary = ReflectionHelper_Shared.ObjectToDictionary<T>(obj, ReflectionHelper_Shared.PARAM_PREFIX);
			SetParameters(command, objPropertyValueDictionary);
		}// SetParameters<T>()

		private static void SetParameters(this SqlCommand command, Dictionary<string, object> objPropertyValueDictionary)
		{
			var paramCol = command.Parameters;

			object propValue;
			string propName;
			int count;

			if (objPropertyValueDictionary.Count > 0)
			{
				var objPropertyValueDictionaryEnumerator = objPropertyValueDictionary.GetEnumerator();
				while (objPropertyValueDictionaryEnumerator.MoveNext())
				{
					var kvp = objPropertyValueDictionaryEnumerator.Current;
					propName = kvp.Key;
					propValue = kvp.Value;

					if (propValue is IEnumerable propValueEnumerable && !(propValue is string s) && !(propValue is byte[] b))
					{
						count = 0;
						//if (propName[0] != '@') propName = '@' + propName;
						foreach (var item in propValueEnumerable)
						{
							++count;
							paramCol.Add(GenerateParameter(parameterName: propName + count.IntToString(), data: item));
						}
						command.CommandText = command.CommandText.Replace(propName, count == 0 ? "SELECT TOP 0 0" : GetParamString(count, propName));
					}
					else
					{
						paramCol.Add(GenerateParameter(parameterName: propName, data: propValue));
					}
				}// while over property dictionary enumerator
			}// if dictionary count > 0
		}// SetParameters()

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetupParameters<TParamType>(this SqlCommand command, TParamType param) where TParamType : class
		{
			if (param != null)
			{
				var dictParam = param as Dictionary<string, object>;
				if (dictParam != null)
					command.SetParameters(dictParam);
				else
					command.SetParameters<TParamType>(param);
			}
		}// SetupParameters<TParamType>()

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static void SetupParameters<TParamType>(this SqlCommand command, TParamType param, Type explicitParamType) where TParamType : class
		{
			if (param != null)
			{
				var dictParam = param as Dictionary<string, object>;
				if (dictParam != null)
					command.SetParameters(dictParam);
				else
					command.SetParameters(param, explicitParamType);
			}
		}// SetupParameters<TParamType>()

		internal const string CTX_PARAMETER_NAME = "@@ctx";
		internal const string CT_PARAMETER_NAME = "@@ct";
		internal const string CI_PARAMETER_NAME = "@@ci";

		internal const string CMD_HEADER_START = "set nocount,xact_abort on;";
		internal const string CMD_HEADER_START_QUERYBATCH = "set xact_abort on;";

		// 112 is [MAX_CONTEXT_INFO_LENGTH minus size_of_ctx_guid, 128-16
		internal const string CMD_HEADER_REST = "set " + CI_PARAMETER_NAME + "=" + CTX_PARAMETER_NAME + "+cast(right(" + CT_PARAMETER_NAME + ",112)as binary(112));set context_info " + CI_PARAMETER_NAME + ";\n";

		internal const string CMD_HEADER = CMD_HEADER_START + CMD_HEADER_REST;
		internal const string CMD_HEADER_QUERYBATCH = CMD_HEADER_START_QUERYBATCH + CMD_HEADER_REST;
		internal const string CMD_FOOTER = ";\nset context_info 0x";

		internal const int MAX_UNICODE_STRING_LENGTH = 4000;
		internal const int MAX_ANSI_STRING_LENGTH = MAX_UNICODE_STRING_LENGTH * 2;
		internal const int MAX_CONTEXT_INFO_LENGTH = 128;

		internal static void SetupMetaParameters(this SqlCommand command, byte[] callerIdentity, string callerMemberName, string callerFilePath, int callerLineNumber)
		{
			// CONTEXT parameter
			{
				var callerIdentityContextParameter = new SqlParameter(CTX_PARAMETER_NAME, SqlDbType.Binary, 16) { Value = callerIdentity };
				command.Parameters.Add(callerIdentityContextParameter);
			}
			// CODETRACE parameter
			{
				var codetraceParameter = new SqlParameter(CT_PARAMETER_NAME, SqlDbType.VarChar, MAX_ANSI_STRING_LENGTH)
				{
					Value = MakeCodeTraceString(callerMemberName, callerFilePath, callerLineNumber)
				};
				command.Parameters.Add(codetraceParameter);
			}
			// CONTEXT_INFO parameter
			{
				var ciParameter = new SqlParameter(CI_PARAMETER_NAME, SqlDbType.VarBinary, MAX_CONTEXT_INFO_LENGTH)
				{
					Value = Util.ZeroLengthArray<byte>.Value
				};
				command.Parameters.Add(ciParameter);
			}
		}// SetupMetaParameters()

		internal static void Setup<TParamType>(
			this SqlCommand command,
			string sql,
			TParamType param,
			byte[] callerIdentity,
			int? commandTimeout = null,
			bool sqlTextOnly = false,
			string callerMemberName = null,
			string callerFilePath = null,
			int callerLineNumber = 0
		) where TParamType : class
		{
			if (!sqlTextOnly)
			{
				command.CommandText = sql;
				SetupParameters(command, param);
				SetupMetaParameters(command, callerIdentity, callerMemberName, callerFilePath, callerLineNumber);

				command.CommandText = string.Concat(CMD_HEADER, command.CommandText, CMD_FOOTER);
			}
			else
			{
				command.CommandText = sql;
			}

			if (commandTimeout.HasValue)
				command.CommandTimeout = commandTimeout.Value;
		}// Setup<TparamType>()

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal static string MakeCodeTraceString(string callerMemberName, string callerFilePath, int callerLineNumber)
		{
			return string.Concat(callerFilePath, callerLineNumber.IntToString(), callerMemberName);
		}

		static string GetParamString(int count, string propName)
		{
			var sb = Utils.StringBuilderCache.Acquire((propName.Length + 1) * (count + 1)).Append(propName).Append('1');
			for (int i = 2; i <= count; ++i)
			{
				sb.Append(',').Append(propName).Append(i.IntToString());
			}
			return Utils.StringBuilderCache.GetStringAndRelease(sb);
		}// GetParamString()

		static readonly Dictionary<Type, DbType> typeMap;
		static CommandExtensions()
		{
			typeMap = new Dictionary<Type, DbType>
			{
				{ typeof(byte),  DbType.Byte },
				{ typeof(sbyte), DbType.SByte },
				{ typeof(short), DbType.Int16 },
				{ typeof(ushort), DbType.UInt16},
				{ typeof(int), DbType.Int32 },
				{ typeof(uint), DbType.UInt32 },
				{ typeof(long), DbType.Int64 },
				{ typeof(ulong), DbType.UInt64 },
				{ typeof(float), DbType.Single },
				{ typeof(double), DbType.Double },
				{ typeof(decimal), DbType.Decimal },
				{ typeof(bool), DbType.Boolean },
				{ typeof(string), DbType.String },
				{ typeof(char), DbType.StringFixedLength },
				{ typeof(Guid), DbType.Guid },
				{ typeof(DateTime), DbType.DateTime2 },
				{ typeof(DateTimeOffset), DbType.DateTimeOffset },
				{ typeof(TimeSpan), DbType.Time },
				{ typeof(byte[]), DbType.Binary },
				{ typeof(byte?), DbType.Byte },
				{ typeof(sbyte?), DbType.SByte },
				{ typeof(short?), DbType.Int16 },
				{ typeof(ushort?), DbType.UInt16 },
				{ typeof(int?), DbType.Int32 },
				{ typeof(uint?), DbType.UInt32 },
				{ typeof(long?), DbType.Int64 },
				{ typeof(ulong?), DbType.UInt64 },
				{ typeof(float?), DbType.Single },
				{ typeof(double?), DbType.Double },
				{ typeof(decimal?), DbType.Decimal },
				{ typeof(bool?), DbType.Boolean },
				{ typeof(char?), DbType.StringFixedLength },
				{ typeof(Guid?), DbType.Guid },
				{ typeof(DateTime?), DbType.DateTime2 },
				{ typeof(DateTimeOffset?), DbType.DateTimeOffset },
				{ typeof(TimeSpan?), DbType.Time },
				{ typeof(Object), DbType.Object }
			};//typeMap
		}//static ctor
	}//class CommandExtensions
}//ns