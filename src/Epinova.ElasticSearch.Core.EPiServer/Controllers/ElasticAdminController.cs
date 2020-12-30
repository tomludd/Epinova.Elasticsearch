﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Mvc;
using Epinova.ElasticSearch.Core.Admin;
using Epinova.ElasticSearch.Core.Contracts;
using Epinova.ElasticSearch.Core.EPiServer.Contracts;
using Epinova.ElasticSearch.Core.EPiServer.Controllers.Abstractions;
using Epinova.ElasticSearch.Core.EPiServer.Models.ViewModels;
using Epinova.ElasticSearch.Core.Models;
using Epinova.ElasticSearch.Core.Models.Admin;
using Epinova.ElasticSearch.Core.Settings;
using Epinova.ElasticSearch.Core.Settings.Configuration;
using Epinova.ElasticSearch.Core.Utilities;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Scheduler;

namespace Epinova.ElasticSearch.Core.EPiServer.Controllers
{
    public class ElasticAdminController : ElasticSearchControllerBase
    {
        private readonly IContentIndexService _contentIndexService;
        private readonly ICoreIndexer _coreIndexer;
        private readonly IElasticSearchSettings _settings;
        private readonly Health _healthHelper;
        private readonly IHttpClientHelper _httpClientHelper;
        private readonly IServerInfoService _serverInfoService;
        private readonly IScheduledJobRepository _scheduledJobRepository;
        private readonly IScheduledJobExecutor _scheduledJobExecutor;

        public ElasticAdminController(
            IContentIndexService contentIndexService,
            ILanguageBranchRepository languageBranchRepository,
            ICoreIndexer coreIndexer,
            IElasticSearchSettings settings,
            IHttpClientHelper httpClientHelper,
            IServerInfoService serverInfoService,
            IScheduledJobRepository scheduledJobRepository,
            IScheduledJobExecutor scheduledJobExecutor)
            : base(serverInfoService, settings, httpClientHelper, languageBranchRepository)
        {
            _contentIndexService = contentIndexService;
            _coreIndexer = coreIndexer;
            _settings = settings;
            _healthHelper = new Health(settings, httpClientHelper);
            _httpClientHelper = httpClientHelper;
            _serverInfoService = serverInfoService;
            _scheduledJobRepository = scheduledJobRepository;
            _scheduledJobExecutor = scheduledJobExecutor;
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult Index()
        {
            HealthInformation clusterHealth = _healthHelper.GetClusterHealth();
            Node[] nodeInfo = _healthHelper.GetNodeInfo();

            var adminViewModel = new AdminViewModel(clusterHealth, Indices.OrderBy(i => i.Type), nodeInfo);

            return View("~/Views/ElasticSearchAdmin/Admin/Index.cshtml", adminViewModel);
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult RunIndexJob()
        {
            var indexJob = _scheduledJobRepository.List().FirstOrDefault(job => job.Name == Constants.IndexEPiServerContentDisplayName);
            if(indexJob != null)
            {
                _scheduledJobExecutor.StartAsync(indexJob);
            }
            return RedirectToAction("Index");
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult AddNewIndex()
        {
            if(_serverInfoService.GetInfo().Version < Constants.MinimumSupportedVersion)
            {
                throw new InvalidOperationException("Elasticsearch version 5 or higher required");
            }

            ElasticSearchSection config = ElasticSearchSection.GetConfiguration();

            foreach(var lang in Languages)
            {
                foreach(IndexConfiguration indexConfig in config.IndicesParsed)
                {
                    var indexName = _settings.GetCustomIndexName(indexConfig.Name, lang.Key);
                    Type indexType = GetIndexType(indexConfig, config);

                    var index = new Index(_serverInfoService, _settings, _httpClientHelper, indexName);
                    if(!index.Exists)
                    {
                        index.Initialize(indexType);
                        index.WaitForStatus();
                    }

                    if(IsCustomType(indexType))
                    {
                        _coreIndexer.UpdateMapping(indexType, indexType, indexName, lang.Key, false);
                        index.WaitForStatus();
                        continue;
                    }
                    
                    UpdateMappingForTypes(ContentReference.RootPage, indexType, indexName, lang.Key);
                    index.WaitForStatus();

                    //TODO really why?
                    //index.DisableDynamicMapping(indexType);
                    //index.WaitForStatus();

                    if(_settings.CommerceEnabled)
                    {
                        string commerceIndexName = _settings.GetCustomIndexName($"{indexConfig.Name}-{Constants.CommerceProviderName}", lang.Key);
                        Index commerceIndex = new Index(_serverInfoService, _settings, _httpClientHelper, commerceIndexName);
                        if(!commerceIndex.Exists)
                        {
                            commerceIndex.Initialize(indexType);
                            commerceIndex.WaitForStatus();

                            ContentReference commerceRoot = GetCommerceRoot(config);
                            UpdateMappingForTypes(commerceRoot, indexType, commerceIndexName, lang.Key);
                            commerceIndex.WaitForStatus();

                            //TODO really why?
                            //commerceIndex.DisableDynamicMapping(indexType);
                            //commerceIndex.WaitForStatus();
                        }
                    }
                }
            }

            return RedirectToAction("Index");
        }

        private ContentReference GetCommerceRoot(ElasticSearchSection config)
        {
            ContentReference commerceRoot = ContentReference.EmptyReference;
            foreach(ContentSelectorConfiguration entry in config.ContentSelector)
            {
                if(entry.Provider == Constants.CommerceProviderName)
                    commerceRoot = new ContentReference(entry.Id);
            }

            if(ContentReference.IsNullOrEmpty(commerceRoot))
                throw new Exception("No commerce root");

            return commerceRoot;
        }

        private void UpdateMappingForTypes(ContentReference rootLink, Type indexType, string indexName, string languageKey)
        {
            List<IContent> allContents = _contentIndexService.ListContentFromRoot(_settings.BulkSize, rootLink, new List<LanguageBranch> { new LanguageBranch(languageKey) });
            Type[] types = _contentIndexService.ListContainedTypes(allContents);

            foreach(Type type in types)
            {
                _coreIndexer.UpdateMapping(type, indexType, indexName, languageKey, false);
            }
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult DeleteIndex(string indexName)
        {
            var indexing = new Indexing(_serverInfoService, _settings, _httpClientHelper);
            indexing.DeleteIndex(indexName);

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult DeleteAll()
        {
            var indexing = new Indexing(_serverInfoService, _settings, _httpClientHelper);

            foreach(var index in Indices)
            {
                indexing.DeleteIndex(index.Index);
            }

            return RedirectToAction("Index");
        }

        [Authorize(Roles = RoleNames.ElasticsearchAdmins)]
        public ActionResult ChangeTokenizer(string indexName, string tokenizer)
        {
            var indexing = new Indexing(_serverInfoService, _settings, _httpClientHelper);
            var index = new Index(_serverInfoService, _settings, _httpClientHelper, indexName);

            indexing.Close(indexName);
            index.ChangeTokenizer(tokenizer);
            indexing.Open(indexName);

            index.WaitForStatus();

            return RedirectToAction("Index");
        }

        private static bool IsCustomType(Type indexType)
            => indexType != null && indexType != typeof(IndexItem);

        private static Type GetIndexType(IndexConfiguration index, ElasticSearchSection config)
        {
            if(index.Default || config.IndicesParsed.Count() == 1)
            {
                return typeof(IndexItem);
            }

            if(String.IsNullOrWhiteSpace(index.Type))
            {
                return null;
            }

            return Type.GetType(index.Type, false, true);
        }
    }
}
