﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Umbraco.Core.Models;
using Umbraco.Core;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Rdbms;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Persistence.UnitOfWork;
using Umbraco.Core.Strings;

namespace Umbraco.Core.Persistence.Repositories
{
    /// <summary>
    /// Represents the EntityRepository used to query <see cref="IUmbracoEntity"/> objects.
    /// </summary>
    /// <remarks>
    /// This is limited to objects that are based in the umbracoNode-table.
    /// </remarks>
    internal class EntityRepository : DisposableObject, IEntityRepository
    {
        private readonly IDatabaseUnitOfWork _work;

        public EntityRepository(IDatabaseUnitOfWork work)
		{
		    _work = work;
		}

        /// <summary>
        /// Returns the Unit of Work added to the repository
        /// </summary>
        protected internal IDatabaseUnitOfWork UnitOfWork
        {
            get { return _work; }
        }

        /// <summary>
        /// Internal for testing purposes
        /// </summary>
        internal Guid UnitKey
        {
            get { return (Guid)_work.Key; }
        }

        #region Query Methods

        public IEnumerable<IUmbracoEntity> GetPagedResultsByQuery(IQuery<IUmbracoEntity> query, Guid objectTypeId, long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, IQuery<IUmbracoEntity> filter = null)
        {   
            bool isContent = objectTypeId == new Guid(Constants.ObjectTypes.Document);
            bool isMedia = objectTypeId == new Guid(Constants.ObjectTypes.Media);
            var factory = new UmbracoEntityFactory();
            
            var sqlClause = GetBaseWhere(GetBase, isContent, isMedia, sql =>
            {
                if (filter != null)
                {
                    foreach (var filterClaus in filter.GetWhereClauses())
                    {
                        sql.Where(filterClaus.Item1, filterClaus.Item2);
                    }
                }
            }, objectTypeId);
            var translator = new SqlTranslator<IUmbracoEntity>(sqlClause, query);
            var entitySql = translator.Translate();
            var pagedSql = entitySql.Append(GetGroupBy(isContent, isMedia, false)).OrderBy("umbracoNode.id");

            IEnumerable<IUmbracoEntity> result;

            if (isMedia)
            {
                //Treat media differently for now, as an Entity it will be returned with ALL of it's properties in the AdditionalData bag!
                var pagedResult = _work.Database.Page<dynamic>(pageIndex + 1, pageSize, pagedSql);
                
                var ids = pagedResult.Items.Select(x => (int) x.id).InGroupsOf(2000);
                var entities = pagedResult.Items.Select(factory.BuildEntityFromDynamic).Cast<IUmbracoEntity>().ToList();

                //Now we need to merge in the property data since we need paging and we can't do this the way that the big media query was working before
                foreach (var idGroup in ids)
                {
                    var propSql = GetPropertySql(Constants.ObjectTypes.Media)
                        .Where("contentNodeId IN (@ids)", new {ids = idGroup})
                        .OrderBy("contentNodeId");

                    //This does NOT fetch all data into memory in a list, this will read
                    // over the records as a data reader, this is much better for performance and memory,
                    // but it means that during the reading of this data set, nothing else can be read
                    // from SQL server otherwise we'll get an exception.
                    var allPropertyData = _work.Database.Query<dynamic>(propSql);

                    //keep track of the current property data item being enumerated
                    var propertyDataSetEnumerator = allPropertyData.GetEnumerator();
                    var hasCurrent = false; // initially there is no enumerator.Current

                    try
                    {
                        //This must be sorted by node id (which is done by SQL) because this is how we are sorting the query to lookup property types above,
                        // which allows us to more efficiently iterate over the large data set of property values.
                        foreach (var entity in entities)
                        {
                            // assemble the dtos for this def
                            // use the available enumerator.Current if any else move to next
                            while (hasCurrent || propertyDataSetEnumerator.MoveNext())
                            {
                                if (propertyDataSetEnumerator.Current.contentNodeId == entity.Id)
                                {
                                    hasCurrent = false; // enumerator.Current is not available

                                    //the property data goes into the additional data
                                    entity.AdditionalData[propertyDataSetEnumerator.Current.propertyTypeAlias] = new UmbracoEntity.EntityProperty
                                    {
                                        PropertyEditorAlias = propertyDataSetEnumerator.Current.propertyEditorAlias,
                                        Value = StringExtensions.IsNullOrWhiteSpace(propertyDataSetEnumerator.Current.dataNtext)
                                            ? propertyDataSetEnumerator.Current.dataNvarchar
                                            : StringExtensions.ConvertToJsonIfPossible(propertyDataSetEnumerator.Current.dataNtext)
                                    };
                                }
                                else
                                {
                                    hasCurrent = true; // enumerator.Current is available for another def
                                    break; // no more propertyDataDto for this def
                                }
                            }
                        }
                    }
                    finally
                    {
                        propertyDataSetEnumerator.Dispose();
                    }
                }
                
                result = entities;
            }
            else
            {   
                var pagedResult = _work.Database.Page<dynamic>(pageIndex + 1, pageSize, pagedSql);
                result = pagedResult.Items.Select(factory.BuildEntityFromDynamic).Cast<IUmbracoEntity>().ToList();
            }

            //The total items from the PetaPoco page query will be wrong due to the Outer join used on parent, depending on the search this will 
            //return duplicate results when the COUNT is used in conjuction with it, so we need to get the total on our own.

            //generate a query that does not contain the LEFT Join for parent, this would cause
            //the COUNT(*) query to return the wrong 
            var sqlCountClause = GetBaseWhere(
                (isC, isM, f) => GetBase(isC, isM, f, true), //true == is a count query
                isContent, isMedia, sql =>
                {
                    if (filter != null)
                    {
                        foreach (var filterClaus in filter.GetWhereClauses())
                        {
                            sql.Where(filterClaus.Item1, filterClaus.Item2);
                        }
                    }
                }, objectTypeId);
            var translatorCount = new SqlTranslator<IUmbracoEntity>(sqlCountClause, query);
            var countSql = translatorCount.Translate();

            totalRecords = _work.Database.ExecuteScalar<int>(countSql);

            return result;
        }

        public IUmbracoEntity GetByKey(Guid key)
        {
            var sql = GetBaseWhere(GetBase, false, false, key);
            var nodeDto = _work.Database.FirstOrDefault<dynamic>(sql);
            if (nodeDto == null)
                return null;

            var factory = new UmbracoEntityFactory();
            var entity = factory.BuildEntityFromDynamic(nodeDto);

            return entity;
        }

        public IUmbracoEntity GetByKey(Guid key, Guid objectTypeId)
        {
            bool isContent = objectTypeId == new Guid(Constants.ObjectTypes.Document);
            bool isMedia = objectTypeId == new Guid(Constants.ObjectTypes.Media);

            var sql = GetFullSqlForEntityType(key, isContent, isMedia, objectTypeId);
            
            if (isMedia)
            {
                //Treat media differently for now, as an Entity it will be returned with ALL of it's properties in the AdditionalData bag!
                var entities = _work.Database.Fetch<dynamic, UmbracoPropertyDto, UmbracoEntity>(
                    new UmbracoEntityRelator().Map, sql);

                return entities.FirstOrDefault();
            }
            else
            {
                var nodeDto = _work.Database.FirstOrDefault<dynamic>(sql);
                if (nodeDto == null)
                    return null;

                var factory = new UmbracoEntityFactory();
                var entity = factory.BuildEntityFromDynamic(nodeDto);

                return entity;
            }
            
            
        }

        public virtual IUmbracoEntity Get(int id)
        {
            var sql = GetBaseWhere(GetBase, false, false, id);
            var nodeDto = _work.Database.FirstOrDefault<dynamic>(sql);
            if (nodeDto == null)
                return null;

            var factory = new UmbracoEntityFactory();
            var entity = factory.BuildEntityFromDynamic(nodeDto);

            return entity;
        }

        public virtual IUmbracoEntity Get(int id, Guid objectTypeId)
        {
            bool isContent = objectTypeId == new Guid(Constants.ObjectTypes.Document);
            bool isMedia = objectTypeId == new Guid(Constants.ObjectTypes.Media);

            var sql = GetFullSqlForEntityType(id, isContent, isMedia, objectTypeId);
            
            if (isMedia)
            {
                //Treat media differently for now, as an Entity it will be returned with ALL of it's properties in the AdditionalData bag!
                var entities = _work.Database.Fetch<dynamic, UmbracoPropertyDto, UmbracoEntity>(
                    new UmbracoEntityRelator().Map, sql);

                return entities.FirstOrDefault();
            }
            else
            {
                var nodeDto = _work.Database.FirstOrDefault<dynamic>(sql);
                if (nodeDto == null)
                    return null;

                var factory = new UmbracoEntityFactory();
                var entity = factory.BuildEntityFromDynamic(nodeDto);

                return entity;
            }

            
        }

        public virtual IEnumerable<IUmbracoEntity> GetAll(Guid objectTypeId, params int[] ids)
        {
            if (ids.Any())
            {
                return PerformGetAll(objectTypeId, sql1 => sql1.Where(" umbracoNode.id in (@ids)", new {ids = ids}));
            }
            else
            {
                return PerformGetAll(objectTypeId);
            }
        }

        public virtual IEnumerable<IUmbracoEntity> GetAll(Guid objectTypeId, params Guid[] keys)
        {
            if (keys.Any())
            {
                return PerformGetAll(objectTypeId, sql1 => sql1.Where(" umbracoNode.uniqueID in (@keys)", new { keys = keys }));
            }
            else
            {
                return PerformGetAll(objectTypeId);
            }
        }

        private IEnumerable<IUmbracoEntity> PerformGetAll(Guid objectTypeId, Action<Sql> filter = null)
        {
            bool isContent = objectTypeId == new Guid(Constants.ObjectTypes.Document);
            bool isMedia = objectTypeId == new Guid(Constants.ObjectTypes.Media);
            var sql = GetFullSqlForEntityType(isContent, isMedia, objectTypeId, filter);

            var factory = new UmbracoEntityFactory();

            if (isMedia)
            {
                //Treat media differently for now, as an Entity it will be returned with ALL of it's properties in the AdditionalData bag!
                var entities = _work.Database.Fetch<dynamic, UmbracoPropertyDto, UmbracoEntity>(
                    new UmbracoEntityRelator().Map, sql);
                foreach (var entity in entities)
                {
                    yield return entity;
                }
            }
            else
            {
                var dtos = _work.Database.Fetch<dynamic>(sql);
                foreach (var entity in dtos.Select(dto => factory.BuildEntityFromDynamic(dto)))
                {
                    yield return entity;
                }
            }
        }


        public virtual IEnumerable<IUmbracoEntity> GetByQuery(IQuery<IUmbracoEntity> query)
        {
            var sqlClause = GetBase(false, false, null);
            var translator = new SqlTranslator<IUmbracoEntity>(sqlClause, query);
            var sql = translator.Translate().Append(GetGroupBy(false, false));

            var dtos = _work.Database.Fetch<dynamic>(sql);

            var factory = new UmbracoEntityFactory();
            var list = dtos.Select(factory.BuildEntityFromDynamic).Cast<IUmbracoEntity>().ToList();

            return list;
        }

        public virtual IEnumerable<IUmbracoEntity> GetByQuery(IQuery<IUmbracoEntity> query, Guid objectTypeId)
        {

            bool isContent = objectTypeId == new Guid(Constants.ObjectTypes.Document);
            bool isMedia = objectTypeId == new Guid(Constants.ObjectTypes.Media);

            var sqlClause = GetBaseWhere(GetBase, isContent, isMedia, null, objectTypeId);
            
            var translator = new SqlTranslator<IUmbracoEntity>(sqlClause, query);
            var entitySql = translator.Translate();

            var factory = new UmbracoEntityFactory();

            if (isMedia)
            {
                var wheres = query.GetWhereClauses().ToArray();

                var mediaSql = GetFullSqlForMedia(entitySql.Append(GetGroupBy(isContent, true, false)), sql =>
                {
                    //adds the additional filters
                    foreach (var whereClause in wheres)
                    {
                        sql.Where(whereClause.Item1, whereClause.Item2);
                    }
                });

                //Treat media differently for now, as an Entity it will be returned with ALL of it's properties in the AdditionalData bag!
                var entities = _work.Database.Fetch<dynamic, UmbracoPropertyDto, UmbracoEntity>(
                    new UmbracoEntityRelator().Map, mediaSql);
                return entities;
            }
            else
            {
                //use dynamic so that we can get ALL properties from the SQL so we can chuck that data into our AdditionalData
                var finalSql = entitySql.Append(GetGroupBy(isContent, false));
                var dtos = _work.Database.Fetch<dynamic>(finalSql);
                return dtos.Select(factory.BuildEntityFromDynamic).Cast<IUmbracoEntity>().ToList();
            }
        }

        #endregion
        

        #region Sql Statements

        protected Sql GetFullSqlForEntityType(Guid key, bool isContent, bool isMedia, Guid objectTypeId)
        {
            var entitySql = GetBaseWhere(GetBase, isContent, isMedia, objectTypeId, key);

            if (isMedia == false) return entitySql.Append(GetGroupBy(isContent, false));

            return GetFullSqlForMedia(entitySql.Append(GetGroupBy(isContent, true, false)));
        }

        protected Sql GetFullSqlForEntityType(int id, bool isContent, bool isMedia, Guid objectTypeId)
        {
            var entitySql = GetBaseWhere(GetBase, isContent, isMedia, objectTypeId, id);

            if (isMedia == false) return entitySql.Append(GetGroupBy(isContent, false));

            return GetFullSqlForMedia(entitySql.Append(GetGroupBy(isContent, true, false)));
        }

        protected Sql GetFullSqlForEntityType(bool isContent, bool isMedia, Guid objectTypeId, Action<Sql> filter)
        {
            var entitySql = GetBaseWhere(GetBase, isContent, isMedia, filter, objectTypeId);

            if (isMedia == false) return entitySql.Append(GetGroupBy(isContent, false));

            return GetFullSqlForMedia(entitySql.Append(GetGroupBy(isContent, true, false)), filter);
        }

        private Sql GetPropertySql(string nodeObjectType)
        {
            var sql = new Sql()
                .Select("contentNodeId, versionId, dataNvarchar, dataNtext, propertyEditorAlias, alias as propertyTypeAlias")
                .From<PropertyDataDto>()
                .InnerJoin<NodeDto>()
                .On<PropertyDataDto, NodeDto>(dto => dto.NodeId, dto => dto.NodeId)
                .InnerJoin<PropertyTypeDto>()
                .On<PropertyTypeDto, PropertyDataDto>(dto => dto.Id, dto => dto.PropertyTypeId)
                .InnerJoin<DataTypeDto>()
                .On<PropertyTypeDto, DataTypeDto>(dto => dto.DataTypeId, dto => dto.DataTypeId)
                .Where("umbracoNode.nodeObjectType = @nodeObjectType", new { nodeObjectType = nodeObjectType });

            return sql;
        }

        private Sql GetFullSqlForMedia(Sql entitySql, Action<Sql> filter = null)
        {
            //this will add any dataNvarchar property to the output which can be added to the additional properties

            var joinSql = GetPropertySql(Constants.ObjectTypes.Media);

            if (filter != null)
            {
                filter(joinSql);
            }

            //We're going to create a query to query against the entity SQL 
            // because we cannot group by nText columns and we have a COUNT in the entitySql we cannot simply left join
            // the entitySql query, we have to join the wrapped query to get the ntext in the result
            
            var wrappedSql = new Sql("SELECT * FROM (")
                .Append(entitySql)
                .Append(new Sql(") tmpTbl LEFT JOIN ("))
                .Append(joinSql)
                .Append(new Sql(") as property ON id = property.contentNodeId"))
                .OrderBy("sortOrder, id");

            return wrappedSql;
        }

        protected virtual Sql GetBase(bool isContent, bool isMedia, Action<Sql> customFilter)
        {
            return GetBase(isContent, isMedia, customFilter, false);
        }

        protected virtual Sql GetBase(bool isContent, bool isMedia, Action<Sql> customFilter, bool isCount)
        {
            var columns = new List<object>();
            if (isCount)
            {
                columns.Add("COUNT(*)");
            }
            else
            {
                columns.AddRange(new List<object>
                {
                    "umbracoNode.id",
                    "umbracoNode.trashed",
                    "umbracoNode.parentID",
                    "umbracoNode.nodeUser",
                    "umbracoNode.level",
                    "umbracoNode.path",
                    "umbracoNode.sortOrder",
                    "umbracoNode.uniqueID",
                    "umbracoNode.text",
                    "umbracoNode.nodeObjectType",
                    "umbracoNode.createDate",
                    "COUNT(parent.parentID) as children"
                });

                if (isContent || isMedia)
                {
                    if (isContent)
                    {
                        //only content has this info
                        columns.Add("published.versionId as publishedVersion");
                        columns.Add("document.versionId as newestVersion");
                    }

                    columns.Add("contenttype.alias");
                    columns.Add("contenttype.icon");
                    columns.Add("contenttype.thumbnail");
                    columns.Add("contenttype.isContainer");
                }
            }

            //Creates an SQL query to return a single row for the entity

            var entitySql = new Sql()
                .Select(columns.ToArray())
                .From("umbracoNode umbracoNode");
                
            if (isContent || isMedia)
            {
                entitySql.InnerJoin("cmsContent content").On("content.nodeId = umbracoNode.id");

                if (isContent)
                {
                    //only content has this info
                    entitySql
                        .InnerJoin("cmsDocument document").On("document.nodeId = umbracoNode.id")
                        .LeftJoin("(SELECT nodeId, versionId FROM cmsDocument WHERE published = 1) as published")
                        .On("umbracoNode.id = published.nodeId");
                }

                entitySql.LeftJoin("cmsContentType contenttype").On("contenttype.nodeId = content.contentType");
            }

            if (isCount == false)
            {
                entitySql.LeftJoin("umbracoNode parent").On("parent.parentID = umbracoNode.id");
            }

            if (customFilter != null)
            {
                customFilter(entitySql);
            }

            return entitySql;
        }

        protected virtual Sql GetBaseWhere(Func<bool, bool, Action<Sql>, Sql> baseQuery, bool isContent, bool isMedia, Action<Sql> filter, Guid nodeObjectType)
        {
            var sql = baseQuery(isContent, isMedia, filter)
                .Where("umbracoNode.nodeObjectType = @NodeObjectType", new { NodeObjectType = nodeObjectType });

            if (isContent)
            {
                sql.Where("document.newest = 1");
            }

            return sql;
        }

        protected virtual Sql GetBaseWhere(Func<bool, bool, Action<Sql>, Sql> baseQuery, bool isContent, bool isMedia, int id)
        {
            var sql = baseQuery(isContent, isMedia, null)
                .Where("umbracoNode.id = @Id", new { Id = id });

            if (isContent)
            {
                sql.Where("document.newest = 1");
            }

            sql.Append(GetGroupBy(isContent, isMedia));

            return sql;
        }

        protected virtual Sql GetBaseWhere(Func<bool, bool, Action<Sql>, Sql> baseQuery, bool isContent, bool isMedia, Guid key)
        {
            var sql = baseQuery(isContent, isMedia, null)
                .Where("umbracoNode.uniqueID = @UniqueID", new { UniqueID = key });

            if (isContent)
            {
                sql.Where("document.newest = 1");
            }

            sql.Append(GetGroupBy(isContent, isMedia));

            return sql;
        }

        protected virtual Sql GetBaseWhere(Func<bool, bool, Action<Sql>, Sql> baseQuery, bool isContent, bool isMedia, Guid nodeObjectType, int id)
        {
            var sql = baseQuery(isContent, isMedia, null)
                .Where("umbracoNode.id = @Id AND umbracoNode.nodeObjectType = @NodeObjectType",
                       new {Id = id, NodeObjectType = nodeObjectType});

            if (isContent)
            {
                sql.Where("document.newest = 1");
            }

            return sql;
        }

        protected virtual Sql GetBaseWhere(Func<bool, bool, Action<Sql>, Sql> baseQuery, bool isContent, bool isMedia, Guid nodeObjectType, Guid key)
        {
            var sql = baseQuery(isContent, isMedia, null)
                .Where("umbracoNode.uniqueID = @UniqueID AND umbracoNode.nodeObjectType = @NodeObjectType",
                       new { UniqueID = key, NodeObjectType = nodeObjectType });

            if (isContent)
            {
                sql.Where("document.newest = 1");
            }

            return sql;
        }

        protected virtual Sql GetGroupBy(bool isContent, bool isMedia, bool includeSort = true)
        {
            var columns = new List<object>
            {
                "umbracoNode.id",
                "umbracoNode.trashed",
                "umbracoNode.parentID",
                "umbracoNode.nodeUser",
                "umbracoNode.level",
                "umbracoNode.path",
                "umbracoNode.sortOrder",
                "umbracoNode.uniqueID",
                "umbracoNode.text",
                "umbracoNode.nodeObjectType",
                "umbracoNode.createDate"
            };

            if (isContent || isMedia)
            {
                if (isContent)
                {
                    columns.Add("published.versionId");
                    columns.Add("document.versionId");
                }
                columns.Add("contenttype.alias");
                columns.Add("contenttype.icon");
                columns.Add("contenttype.thumbnail");
                columns.Add("contenttype.isContainer");
            }
            
            var sql = new Sql()
                .GroupBy(columns.ToArray());

            if (includeSort)
            {
                sql = sql.OrderBy("umbracoNode.sortOrder");
            }

            return sql;
        }
        
        #endregion

        /// <summary>
        /// Dispose disposable properties
        /// </summary>
        /// <remarks>
        /// Ensure the unit of work is disposed
        /// </remarks>
        protected override void DisposeResources()
        {
            UnitOfWork.DisposeIfDisposable();
        }

        public bool Exists(Guid key)
        {
            var sql = new Sql().Select("COUNT(*)").From("umbracoNode").Where("uniqueID=@uniqueID", new {uniqueID = key});
            return _work.Database.ExecuteScalar<int>(sql) > 0;            
        }

        public bool Exists(int id)
        {
            var sql = new Sql().Select("COUNT(*)").From("umbracoNode").Where("id=@id", new { id = id });
            return _work.Database.ExecuteScalar<int>(sql) > 0;
        }

        #region umbracoNode POCO - Extends NodeDto
        [TableName("umbracoNode")]
        [PrimaryKey("id")]
        [ExplicitColumns]
        internal class UmbracoEntityDto : NodeDto
        {
            [Column("children")]
            public int Children { get; set; }

            [Column("publishedVersion")]
            public Guid PublishedVersion { get; set; }

            [Column("newestVersion")]
            public Guid NewestVersion { get; set; }

            [Column("alias")]
            public string Alias { get; set; }

            [Column("icon")]
            public string Icon { get; set; }

            [Column("thumbnail")]
            public string Thumbnail { get; set; }

            [ResultColumn]
            public List<UmbracoPropertyDto> UmbracoPropertyDtos { get; set; }
        }
        
        [ExplicitColumns]
        internal class UmbracoPropertyDto
        {
            [Column("propertyEditorAlias")]
            public string PropertyEditorAlias { get; set; }

            [Column("propertyTypeAlias")]
            public string PropertyAlias { get; set; }

            [Column("dataNvarchar")]
            public string NVarcharValue { get; set; }

            [Column("dataNtext")]
            public string NTextValue { get; set; }
        }

        /// <summary>
        /// This is a special relator in that it is not returning a DTO but a real resolved entity and that it accepts
        /// a dynamic instance.
        /// </summary>
        /// <remarks>
        /// We're doing this because when we query the db, we want to use dynamic so that it returns all available fields not just the ones
        ///     defined on the entity so we can them to additional data
        /// </remarks>
        internal class UmbracoEntityRelator
        {
            internal UmbracoEntity Current;
            private readonly UmbracoEntityFactory _factory = new UmbracoEntityFactory();

            internal UmbracoEntity Map(dynamic a, UmbracoPropertyDto p)
            {
                // Terminating call.  Since we can return null from this function
                // we need to be ready for PetaPoco to callback later with null
                // parameters
                if (a == null)
                    return Current;

                // Is this the same UmbracoEntity as the current one we're processing
                if (Current != null && Current.Key == a.uniqueID)
                {
                    if (p != null && p.PropertyAlias.IsNullOrWhiteSpace() == false)
                    {
                        // Add this UmbracoProperty to the current additional data
                        Current.AdditionalData[p.PropertyAlias] = new UmbracoEntity.EntityProperty
                        {
                            PropertyEditorAlias = p.PropertyEditorAlias,
                            Value = p.NTextValue.IsNullOrWhiteSpace()
                                ? p.NVarcharValue
                                : p.NTextValue.ConvertToJsonIfPossible()
                        };    
                    }

                    // Return null to indicate we're not done with this UmbracoEntity yet
                    return null;
                }

                // This is a different UmbracoEntity to the current one, or this is the 
                // first time through and we don't have a Tab yet

                // Save the current UmbracoEntityDto
                var prev = Current;

                // Setup the new current UmbracoEntity
                
                Current = _factory.BuildEntityFromDynamic(a);

                if (p != null && p.PropertyAlias.IsNullOrWhiteSpace() == false)
                {
                    //add the property/create the prop list if null
                    Current.AdditionalData[p.PropertyAlias] = new UmbracoEntity.EntityProperty
                    {
                        PropertyEditorAlias = p.PropertyEditorAlias,
                        Value = p.NTextValue.IsNullOrWhiteSpace()
                            ? p.NVarcharValue
                            : p.NTextValue.ConvertToJsonIfPossible()
                    };
                }

                // Return the now populated previous UmbracoEntity (or null if first time through)
                return prev;
            }
        }
        #endregion
    }
}