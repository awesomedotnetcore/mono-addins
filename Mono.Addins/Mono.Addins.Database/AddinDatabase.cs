//
// AddinDatabase.cs
//
// Author:
//   Lluis Sanchez Gual
//
// Copyright (C) 2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Threading;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Xml;
using System.Reflection;
using Mono.Addins.Description;
using System.Collections.Generic;
using System.Linq;

namespace Mono.Addins.Database
{
	class AddinDatabase
	{
		public const string GlobalDomain = "global";
		public const string UnknownDomain = "unknown";
		
		public const string VersionTag = "001";

		List<Addin> allSetupInfos;
		List<Addin> addinSetupInfos;
		List<Addin> rootSetupInfos;
		internal static bool RunningSetupProcess;
		bool fatalDatabseError;
		Hashtable cachedAddinSetupInfos = new Hashtable ();
		AddinScanResult currentScanResult;
		AddinHostIndex hostIndex;
		FileDatabase fileDatabase;
		string addinDbDir;
		DatabaseConfiguration config = null;
		AddinRegistry registry;
		int lastDomainId;
		AddinEngine addinEngine;
		AddinFileSystemExtension fs = new AddinFileSystemExtension ();
		List<object> extensions = new List<object> ();
		
		public AddinDatabase (AddinEngine addinEngine, AddinRegistry registry)
		{
			this.addinEngine = addinEngine;
			this.registry = registry;
			addinDbDir = Path.Combine (registry.AddinCachePath, "addin-db-" + VersionTag);
			fileDatabase = new FileDatabase (AddinDbDir);
		}
		
		string AddinDbDir {
			get { return addinDbDir; }
		}
		
		public AddinFileSystemExtension FileSystem {
			get { return fs; }
		}
		
		public string AddinCachePath {
			get { return Path.Combine (AddinDbDir, "addin-data"); }
		}
		
		public string AddinFolderCachePath {
			get { return Path.Combine (AddinDbDir, "addin-dir-data"); }
		}
		
		public string AddinPrivateDataPath {
			get { return Path.Combine (AddinDbDir, "addin-priv-data"); }
		}
		
		public string HostsPath {
			get { return Path.Combine (AddinDbDir, "hosts"); }
		}
		
		string HostIndexFile {
			get { return Path.Combine (AddinDbDir, "host-index"); }
		}
		
		string ConfigFile {
			get { return Path.Combine (AddinDbDir, "config.xml"); }
		}
		
		internal bool IsGlobalRegistry {
			get {
				return registry.RegistryPath == AddinRegistry.GlobalRegistryPath;
			}
		}
		
		public AddinRegistry Registry {
			get {
				return this.registry;
			}
		}
		
		public void Clear ()
		{
			if (Directory.Exists (AddinCachePath))
				Directory.Delete (AddinCachePath, true);
			if (Directory.Exists (AddinFolderCachePath))
				Directory.Delete (AddinFolderCachePath, true);
		}
		
		public void CopyExtensions (AddinDatabase other)
		{
			foreach (object o in other.extensions)
				RegisterExtension (o);
		}
		
		public void RegisterExtension (object extension)
		{
			extensions.Add (extension);
			if (extension is AddinFileSystemExtension)
				fs = (AddinFileSystemExtension) extension;
			else
				throw new NotSupportedException ();
		}
		
		public void UnregisterExtension (object extension)
		{
			extensions.Remove (extension);
			if ((extension as AddinFileSystemExtension) == fs)
				fs = new AddinFileSystemExtension ();
			else
				throw new InvalidOperationException ();
		}
		
		public ExtensionNodeSet FindNodeSet (string domain, string addinId, string id)
		{
			return FindNodeSet (domain, addinId, id, new Hashtable ());
		}
		
		ExtensionNodeSet FindNodeSet (string domain, string addinId, string id, Hashtable visited)
		{
			if (visited.Contains (addinId))
				return null;
			visited.Add (addinId, addinId);
			Addin addin = GetInstalledAddin (domain, addinId, true, false);
			if (addin == null)
				return null;
			AddinDescription desc = addin.Description;
			if (desc == null)
				return null;
			foreach (ExtensionNodeSet nset in desc.ExtensionNodeSets)
				if (nset.Id == id)
					return nset;
			
			// Not found in the add-in. Look on add-ins on which it depends
			
			foreach (Dependency dep in desc.MainModule.Dependencies) {
				AddinDependency adep = dep as AddinDependency;
				if (adep == null) continue;
				
				string aid = Addin.GetFullId (desc.Namespace, adep.AddinId, adep.Version);
				ExtensionNodeSet nset = FindNodeSet (domain, aid, id, visited);
				if (nset != null)
					return nset;
			}
			return null;
		}
		
		public IEnumerable<Addin> GetInstalledAddins (string domain, AddinSearchFlags flags)
		{
			if (domain == null)
				domain = registry.CurrentDomain;
			
			// Get the cached list if the add-in list has already been loaded.
			// The domain doesn't have to be checked again, since it is always the same
			
			IEnumerable<Addin> result = null;
			
			if ((flags & AddinSearchFlags.IncludeAll) == AddinSearchFlags.IncludeAll) {
				if (allSetupInfos != null)
					result = allSetupInfos;
			}
			else if ((flags & AddinSearchFlags.IncludeAddins) == AddinSearchFlags.IncludeAddins) {
				if (addinSetupInfos != null)
					result = addinSetupInfos;
			}
			else {
				if (rootSetupInfos != null)
					result = rootSetupInfos;
			}
			
			if (result == null) {
				InternalCheck (domain);
				using (fileDatabase.LockRead ()) {
					result = InternalGetInstalledAddins (domain, null, flags & ~AddinSearchFlags.LatestVersionsOnly);
				}
			}
			
			if ((flags & AddinSearchFlags.LatestVersionsOnly) == AddinSearchFlags.LatestVersionsOnly)
				return result.Where (a => a.IsLatestVersion);
			else
				return result;
		}
		
		IEnumerable<Addin> InternalGetInstalledAddins (string domain, AddinSearchFlags type)
		{
			return InternalGetInstalledAddins (domain, null, type);
		}
		
		IEnumerable<Addin> InternalGetInstalledAddins (string domain, string idFilter, AddinSearchFlags type)
		{
			if ((type & AddinSearchFlags.LatestVersionsOnly) != 0)
				throw new InvalidOperationException ("LatestVersionsOnly flag not supported here");
			
			if (allSetupInfos == null) {
				List<Addin> alist = new List<Addin> ();

				// Global add-ins are valid for any private domain
				if (domain != AddinDatabase.GlobalDomain)
					FindInstalledAddins (alist, AddinDatabase.GlobalDomain, idFilter);

				FindInstalledAddins (alist, domain, idFilter);
				UpdateLastVersionFlags (alist);
				if (idFilter != null)
					return alist;
				allSetupInfos = alist;
			}
			if ((type & AddinSearchFlags.IncludeAll) == AddinSearchFlags.IncludeAll)
				return FilterById (allSetupInfos, idFilter);
			
			if ((type & AddinSearchFlags.IncludeAddins) == AddinSearchFlags.IncludeAddins) {
				if (addinSetupInfos == null) {
					addinSetupInfos = new List<Addin> ();
					foreach (Addin adn in allSetupInfos)
						if (!adn.Description.IsRoot)
							addinSetupInfos.Add (adn);
				}
				return FilterById (addinSetupInfos, idFilter);
			}
			else {
				if (rootSetupInfos == null) {
					rootSetupInfos = new List<Addin> ();
					foreach (Addin adn in allSetupInfos)
						if (adn.Description.IsRoot)
							rootSetupInfos.Add (adn);
				}
				return FilterById (rootSetupInfos, idFilter);
			}
		}
		
		IEnumerable<Addin> FilterById (List<Addin> addins, string id)
		{
			if (id == null)
				return addins;
			return addins.Where (a => Addin.GetIdName (a.Id) == id);
		}

		void FindInstalledAddins (List<Addin> result, string domain, string idFilter)
		{
			if (idFilter == null) 
				idFilter = "*";
			string dir = Path.Combine (AddinCachePath, domain);
			if (Directory.Exists (dir)) {
				foreach (string file in fileDatabase.GetDirectoryFiles (dir, idFilter + ",*.maddin")) {
					string id = Path.GetFileNameWithoutExtension (file);
					result.Add (GetInstalledDomainAddin (domain, id, true, false, false));
				}
			}
		}
		
		void UpdateLastVersionFlags (List<Addin> addins)
		{
			Dictionary<string,string> versions = new Dictionary<string, string> ();
			foreach (Addin a in addins) {
				string last;
				string id, version;
				Addin.GetIdParts (a.Id, out id, out version);
				if (!versions.TryGetValue (id, out last) || Addin.CompareVersions (last, version) > 0)
					versions [id] = version;
			}
			foreach (Addin a in addins) {
				string id, version;
				Addin.GetIdParts (a.Id, out id, out version);
				a.IsLatestVersion = versions [id] == version;
			}
		}

		public Addin GetInstalledAddin (string domain, string id)
		{
			return GetInstalledAddin (domain, id, false, false);
		}
		
		public Addin GetInstalledAddin (string domain, string id, bool exactVersionMatch)
		{
			return GetInstalledAddin (domain, id, exactVersionMatch, false);
		}
		
		public Addin GetInstalledAddin (string domain, string id, bool exactVersionMatch, bool enabledOnly)
		{
			// Try the given domain, and if not found, try the shared domain
			Addin ad = GetInstalledDomainAddin (domain, id, exactVersionMatch, enabledOnly, true);
			if (ad != null)
				return ad;
			if (domain != AddinDatabase.GlobalDomain)
				return GetInstalledDomainAddin (AddinDatabase.GlobalDomain, id, exactVersionMatch, enabledOnly, true);
			else
				return null;
		}
		
		Addin GetInstalledDomainAddin (string domain, string id, bool exactVersionMatch, bool enabledOnly, bool dbLockCheck)
		{
			Addin sinfo = null;
			string idd = id + " " + domain;
			object ob = cachedAddinSetupInfos [idd];
			if (ob != null) {
				sinfo = ob as Addin;
				if (sinfo != null) {
					if (!enabledOnly || sinfo.Enabled)
						return sinfo;
					if (exactVersionMatch)
						return null;
				}
				else if (enabledOnly)
					// Ignore the 'not installed' flag when disabled add-ins are allowed
					return null;
			}
		
			if (dbLockCheck)
				InternalCheck (domain);
			
			using ((dbLockCheck ? fileDatabase.LockRead () : null))
			{
				string path = GetDescriptionPath (domain, id);
				if (sinfo == null && fileDatabase.Exists (path)) {
					sinfo = new Addin (this, path);
					cachedAddinSetupInfos [idd] = sinfo;
					if (!enabledOnly || sinfo.Enabled)
						return sinfo;
					if (exactVersionMatch) {
						// Cache lookups with negative result
						cachedAddinSetupInfos [idd] = this;
						return null;
					}
				}
				
				// Exact version not found. Look for a compatible version
				if (!exactVersionMatch) {
					sinfo = null;
					string version, name, bestVersion = null;
					Addin.GetIdParts (id, out name, out version);
					
					foreach (Addin ia in InternalGetInstalledAddins (domain, name, AddinSearchFlags.IncludeAll)) 
					{
						if ((!enabledOnly || ia.Enabled) &&
						    (version.Length == 0 || ia.SupportsVersion (version)) && 
						    (bestVersion == null || Addin.CompareVersions (bestVersion, ia.Version) > 0)) 
						{
							bestVersion = ia.Version;
							sinfo = ia;
						}
					}
					if (sinfo != null) {
						cachedAddinSetupInfos [idd] = sinfo;
						return sinfo;
					}
				}
				
				// Cache lookups with negative result
				// Ignore the 'not installed' flag when disabled add-ins are allowed
				if (enabledOnly)
					cachedAddinSetupInfos [idd] = this;
				return null;
			}
		}
		
		public void Shutdown ()
		{
			ResetCachedData ();
		}
		
		public Addin GetAddinForHostAssembly (string domain, string assemblyLocation)
		{
			InternalCheck (domain);
			Addin ainfo = null;
			
			object ob = cachedAddinSetupInfos [assemblyLocation];
			if (ob != null)
				return ob as Addin; // Don't use a cast here is ob may not be an Addin.

			AddinHostIndex index = GetAddinHostIndex ();
			string addin, addinFile, rdomain;
			if (index.GetAddinForAssembly (assemblyLocation, out addin, out addinFile, out rdomain)) {
				string sid = addin + " " + rdomain;
				ainfo = cachedAddinSetupInfos [sid] as Addin;
				if (ainfo == null)
					ainfo = new Addin (this, GetDescriptionPath (rdomain, addin));
				cachedAddinSetupInfos [assemblyLocation] = ainfo;
				cachedAddinSetupInfos [addin + " " + rdomain] = ainfo;
			}
			
			return ainfo;
		}
		
		
		public bool IsAddinEnabled (string domain, string id)
		{
			Addin ainfo = GetInstalledAddin (domain, id);
			if (ainfo != null)
				return ainfo.Enabled;
			else
				return false;
		}
		
		internal bool IsAddinEnabled (string domain, string id, bool exactVersionMatch)
		{
			if (!exactVersionMatch)
				return IsAddinEnabled (domain, id);
			Addin ainfo = GetInstalledAddin (domain, id, exactVersionMatch, false);
			if (ainfo == null)
				return false;
			return Configuration.IsEnabled (id, ainfo.AddinInfo.EnabledByDefault);
		}
		
		public void EnableAddin (string domain, string id)
		{
			EnableAddin (domain, id, true);
		}
		
		internal void EnableAddin (string domain, string id, bool exactVersionMatch)
		{
			Addin ainfo = GetInstalledAddin (domain, id, exactVersionMatch, false);
			if (ainfo == null)
				// It may be an add-in root
				return;

			if (IsAddinEnabled (domain, id))
				return;
			
			// Enable required add-ins
			
			foreach (Dependency dep in ainfo.AddinInfo.Dependencies) {
				if (dep is AddinDependency) {
					AddinDependency adep = dep as AddinDependency;
					string adepid = Addin.GetFullId (ainfo.AddinInfo.Namespace, adep.AddinId, adep.Version);
					EnableAddin (domain, adepid, false);
				}
			}

			Configuration.SetStatus (id, true, ainfo.AddinInfo.EnabledByDefault);
			SaveConfiguration ();

			if (addinEngine != null && addinEngine.IsInitialized)
				addinEngine.ActivateAddin (id);
		}
		
		public void DisableAddin (string domain, string id)
		{
			Addin ai = GetInstalledAddin (domain, id, true);
			if (ai == null)
				throw new InvalidOperationException ("Add-in '" + id + "' not installed.");

			if (!IsAddinEnabled (domain, id))
				return;
			
			Configuration.SetStatus (id, false, ai.AddinInfo.EnabledByDefault);
			SaveConfiguration ();
			
			// Disable all add-ins which depend on it
			
			try {
				string idName = Addin.GetIdName (id);
				
				foreach (Addin ainfo in GetInstalledAddins (domain, AddinSearchFlags.IncludeAddins)) {
					foreach (Dependency dep in ainfo.AddinInfo.Dependencies) {
						AddinDependency adep = dep as AddinDependency;
						if (adep == null)
							continue;
						
						string adepid = Addin.GetFullId (ainfo.AddinInfo.Namespace, adep.AddinId, null);
						if (adepid != idName)
							continue;
						
						// The add-in that has been disabled, might be a requeriment of this one, or maybe not
						// if there is an older version available. Check it now.
						
						adepid = Addin.GetFullId (ainfo.AddinInfo.Namespace, adep.AddinId, adep.Version);
						Addin adepinfo = GetInstalledAddin (domain, adepid, false, true);
						
						if (adepinfo == null) {
							DisableAddin (domain, ainfo.Id);
							break;
						}
					}
				}
			}
			catch {
				// If something goes wrong, enable the add-in again
				Configuration.SetStatus (id, true, ai.AddinInfo.EnabledByDefault);
				SaveConfiguration ();
				throw;
			}

			if (addinEngine != null && addinEngine.IsInitialized)
				addinEngine.UnloadAddin (id);
		}		

		internal string GetDescriptionPath (string domain, string id)
		{
			return Path.Combine (Path.Combine (AddinCachePath, domain), id + ".maddin");
		}
		
		void InternalCheck (string domain)
		{
			// If the database is broken, don't try to regenerate it at every check.
			if (fatalDatabseError)
				return;

			bool update = false;
			using (fileDatabase.LockRead ()) {
				if (!Directory.Exists (AddinCachePath)) {
					update = true;
				}
			}
			if (update)
				Update (null, domain);
		}
		
		void GenerateAddinExtensionMapsInternal (IProgressStatus monitor, ArrayList addinsToUpdate, ArrayList addinsToUpdateRelations, ArrayList removedAddins)
		{
			AddinUpdateData updateData = new AddinUpdateData (this, monitor);
			
			// Clear cached data
			cachedAddinSetupInfos.Clear ();
			
			// Collect all information
			
			AddinIndex addinHash = new AddinIndex ();
			
			if (monitor.LogLevel > 1)
				monitor.Log ("Generating add-in extension maps");
			
			Hashtable changedAddins = null;
			ArrayList descriptionsToSave = new ArrayList ();
			ArrayList files = new ArrayList ();
			
			bool partialGeneration = addinsToUpdate != null;
			string[] domains = GetDomains ();
			
			// Get the files to be updated
			
			if (partialGeneration) {
				changedAddins = new Hashtable ();
				
				if (monitor.LogLevel > 2)
					monitor.Log ("Doing a partial registry update.\nAdd-ins to be updated:");
				// Get the files and ids of all add-ins that have to be updated
				foreach (string sa in addinsToUpdate) {
					changedAddins [sa] = sa;
					if (monitor.LogLevel > 2)
						monitor.Log (" - " + sa);
					foreach (string file in GetAddinFiles (sa, domains)) {
						if (!files.Contains (file)) {
							files.Add (file);
							string an = Path.GetFileNameWithoutExtension (file);
							changedAddins [an] = an;
							if (monitor.LogLevel > 2 && an != sa)
								monitor.Log (" - " + an);
						}
					}
				}
				
				if (monitor.LogLevel > 2)
					monitor.Log ("Add-ins whose relations have to be updated:");
				
				// Get the files and ids of all add-ins whose relations have to be updated
				foreach (string sa in addinsToUpdateRelations) {
					foreach (string file in GetAddinFiles (sa, domains)) {
						if (!files.Contains (file)) {
							if (monitor.LogLevel > 2) {
								string an = Path.GetFileNameWithoutExtension (file);
								monitor.Log (" - " + an);
							}
							files.Add (file);
						}
					}
				}
				
				foreach (string s in removedAddins)
					changedAddins [s] = s;
			}
			else {
				foreach (string dom in domains)
					files.AddRange (fileDatabase.GetDirectoryFiles (Path.Combine (AddinCachePath, dom), "*.maddin"));
			}
			
			// Load the descriptions.
			foreach (string file in files) {
			
				AddinDescription conf;
				if (!ReadAddinDescription (monitor, file, out conf)) {
					SafeDelete (monitor, file);
					continue;
				}

				// If the original file does not exist, the description can be deleted
				if (!fs.FileExists (conf.AddinFile)) {
					SafeDelete (monitor, file);
					continue;
				}
				
				// Remove old data from the description. Remove the data of the add-ins that
				// have changed. This data will be re-added later.
				
				conf.UnmergeExternalData (changedAddins);
				descriptionsToSave.Add (conf);
				
				addinHash.Add (conf);
			}

			// Sort the add-ins, to make sure add-ins are processed before
			// all their dependencies
			
			var sorted = addinHash.GetSortedAddins ();
			
			// Register extension points and node sets
			foreach (AddinDescription conf in sorted)
				CollectExtensionPointData (conf, updateData);
			
			if (monitor.LogLevel > 2)
				monitor.Log ("Registering new extensions:");
			
			// Register extensions
			foreach (AddinDescription conf in sorted) {
				if (changedAddins == null || changedAddins.ContainsKey (conf.AddinId)) {
					if (monitor.LogLevel > 2)
						monitor.Log ("- " + conf.AddinId + " (" + conf.Domain + ")");
					CollectExtensionData (monitor, addinHash, conf, updateData);
				}
			}
			
			// Save the maps
			foreach (AddinDescription conf in descriptionsToSave) {
				ConsolidateExtensions (conf);
				conf.SaveBinary (fileDatabase);
			}
			
			if (monitor.LogLevel > 1) {
				monitor.Log ("Addin relation map generated.");
				monitor.Log ("  Addins Updated: " + descriptionsToSave.Count);
				monitor.Log ("  Extension points: " + updateData.RelExtensionPoints);
				monitor.Log ("  Extensions: " + updateData.RelExtensions);
				monitor.Log ("  Extension nodes: " + updateData.RelExtensionNodes);
				monitor.Log ("  Node sets: " + updateData.RelNodeSetTypes);
			}
		}
		
		void ConsolidateExtensions (AddinDescription conf)
		{
			// Merges extensions with the same path
			
			foreach (ModuleDescription module in conf.AllModules) {
				Dictionary<string,Extension> extensions = new Dictionary<string, Extension> ();
				foreach (Extension ext in module.Extensions) {
					Extension mainExt;
					if (extensions.TryGetValue (ext.Path, out mainExt)) {
						ArrayList list = new ArrayList ();
						EnsureInsertionsSorted (ext.ExtensionNodes);
						list.AddRange (ext.ExtensionNodes);
						int pos = -1;
						foreach (ExtensionNodeDescription node in list) {
							ext.ExtensionNodes.Remove (node);
							AddNodeSorted (mainExt.ExtensionNodes, node, ref pos);
						}
					} else {
						extensions [ext.Path] = ext;
						EnsureInsertionsSorted (ext.ExtensionNodes);
					}
				}
				
				// Sort the nodes
			}
		}
		
		void EnsureInsertionsSorted (ExtensionNodeDescriptionCollection list)
		{
			// Makes sure that the nodes in the collections are properly sorted wrt insertafter and insertbefore attributes
			Dictionary<string,ExtensionNodeDescription> added = new Dictionary<string, ExtensionNodeDescription> ();
			List<ExtensionNodeDescription> halfSorted = new List<ExtensionNodeDescription> ();
			bool orderChanged = false;
			
			for (int n = list.Count - 1; n >= 0; n--) {
				ExtensionNodeDescription node = list [n];
				if (node.Id.Length > 0)
					added [node.Id] = node;
				if (node.InsertAfter.Length > 0) {
					ExtensionNodeDescription relNode;
					if (added.TryGetValue (node.InsertAfter, out relNode)) {
						// Out of order. Move it before the referenced node
						int i = halfSorted.IndexOf (relNode);
						halfSorted.Insert (i, node);
						orderChanged = true;
					} else {
						halfSorted.Add (node);
					}
				} else
					halfSorted.Add (node);
			}
			halfSorted.Reverse ();
			List<ExtensionNodeDescription> fullSorted = new List<ExtensionNodeDescription> ();
			added.Clear ();
			
			foreach (ExtensionNodeDescription node in halfSorted) {
				if (node.Id.Length > 0)
					added [node.Id] = node;
				if (node.InsertBefore.Length > 0) {
					ExtensionNodeDescription relNode;
					if (added.TryGetValue (node.InsertBefore, out relNode)) {
						// Out of order. Move it before the referenced node
						int i = fullSorted.IndexOf (relNode);
						fullSorted.Insert (i, node);
						orderChanged = true;
					} else {
						fullSorted.Add (node);
					}
				} else
					fullSorted.Add (node);
			}
			if (orderChanged) {
				list.Clear ();
				foreach (ExtensionNodeDescription node in fullSorted)
					list.Add (node);
			}
		}
		
		void AddNodeSorted (ExtensionNodeDescriptionCollection list, ExtensionNodeDescription node, ref int curPos)
		{
			// Adds the node at the correct position, taking into account insertbefore and insertafter
			
			if (node.InsertAfter.Length > 0) {
				string afterId = node.InsertAfter;
				for (int n=0; n<list.Count; n++) {
					if (list[n].Id == afterId) {
						list.Insert (n + 1, node);
						curPos = n + 2;
						return;
					}
				}
			}
			else if (node.InsertBefore.Length > 0) {
				string beforeId = node.InsertBefore;
				for (int n=0; n<list.Count; n++) {
					if (list[n].Id == beforeId) {
						list.Insert (n, node);
						curPos = n + 1;
						return;
					}
				}
			}
			if (curPos == -1)
				list.Add (node);
			else
				list.Insert (curPos++, node);
		}

		
		IEnumerable GetAddinFiles (string fullId, string[] domains)
		{
			// Look for all versions of the add-in, because this id may be the id of a reference,
			// and the exact reference version may not be installed.
			string s = fullId;
			int i = s.LastIndexOf (',');
			if (i != -1)
				s = s.Substring (0, i);
			s += ",*";
			
			// Look for the add-in in any of the existing folders
			foreach (string domain in domains) {
				string mp = GetDescriptionPath (domain, s);
				string dir = Path.GetDirectoryName (mp);
				string pat = Path.GetFileName (mp);
				foreach (string fmp in fileDatabase.GetDirectoryFiles (dir, pat))
					yield return fmp;
			}
		}
		
		// Collects extension data in a hash table. The key is the path, the value is a list
		// of add-ins ids that extend that path
		
		void CollectExtensionPointData (AddinDescription conf, AddinUpdateData updateData)
		{
			foreach (ExtensionNodeSet nset in conf.ExtensionNodeSets) {
				try {
					updateData.RegisterNodeSet (conf, nset);
					updateData.RelNodeSetTypes++;
				} catch (Exception ex) {
					throw new InvalidOperationException ("Error reading node set: " + nset.Id, ex);
				}
			}
			
			foreach (ExtensionPoint ep in conf.ExtensionPoints) {
				try {
					updateData.RegisterExtensionPoint (conf, ep);
					updateData.RelExtensionPoints++;
				} catch (Exception ex) {
					throw new InvalidOperationException ("Error reading extension point: " + ep.Path, ex);
				}
			}
		}
		
		void CollectExtensionData (IProgressStatus monitor, AddinIndex addinHash, AddinDescription conf, AddinUpdateData updateData)
		{
			IEnumerable<string> missingDeps = addinHash.GetMissingDependencies (conf, conf.MainModule);
			if (missingDeps.Any ()) {
				string w = "The add-in '" + conf.AddinId + "' could not be updated because some of its dependencies are missing or not compatible:";
				w += BuildMissingAddinsList (addinHash, conf, missingDeps);
				monitor.ReportWarning (w);
				return;
			}
			
			CollectModuleExtensionData (conf, conf.MainModule, updateData);
			
			foreach (ModuleDescription module in conf.OptionalModules) {
				missingDeps = addinHash.GetMissingDependencies (conf, module);
				if (missingDeps.Any ()) {
					if (monitor.LogLevel > 1) {
						string w = "An optional module of the add-in '" + conf.AddinId + "' could not be updated because some of its dependencies are missing or not compatible:";
						w += BuildMissingAddinsList (addinHash, conf, missingDeps);
					}
				}
				else
					CollectModuleExtensionData (conf, module, updateData);
			}
		}
		
		string BuildMissingAddinsList (AddinIndex addinHash, AddinDescription conf, IEnumerable<string> missingDeps)
		{
			string w = "";
			foreach (string dep in missingDeps) {
				var found = addinHash.GetSimilarExistingAddin (conf, dep);
				if (found == null)
					w += "\n  missing: " + dep;
				else
					w += "\n  required: " + dep + ", found: " + found.AddinId;
			}
			return w;
		}
		
		void CollectModuleExtensionData (AddinDescription conf, ModuleDescription module, AddinUpdateData updateData)
		{
			foreach (Extension ext in module.Extensions) {
				updateData.RelExtensions++;
				updateData.RegisterExtension (conf, module, ext);
				AddChildExtensions (conf, module, updateData, ext.Path, ext.ExtensionNodes, false);
			}
		}
		
		void AddChildExtensions (AddinDescription conf, ModuleDescription module, AddinUpdateData updateData, string path, ExtensionNodeDescriptionCollection nodes, bool conditionChildren)
		{
			// Don't register conditions as extension nodes.
			if (!conditionChildren)
				updateData.RegisterExtension (conf, module, path);
			
			foreach (ExtensionNodeDescription node in nodes) {
				if (node.NodeName == "ComplexCondition")
					continue;
				updateData.RelExtensionNodes++;
				string id = node.GetAttribute ("id");
				if (id.Length != 0)
					AddChildExtensions (conf, module, updateData, path + "/" + id, node.ChildNodes, node.NodeName == "Condition");
			}
		}
		
		string[] GetDomains ()
		{
			string[] dirs = fileDatabase.GetDirectories (AddinCachePath);
			string[] ids = new string [dirs.Length];
			for (int n=0; n<dirs.Length; n++)
				ids [n] = Path.GetFileName (dirs [n]);
			return ids;
		}

		public string GetUniqueDomainId ()
		{
			if (lastDomainId != 0) {
				lastDomainId++;
				return lastDomainId.ToString ();
			}
			lastDomainId = 1;
			foreach (string s in fileDatabase.GetDirectories (AddinCachePath)) {
				string dn = Path.GetFileName (s);
				if (dn == GlobalDomain)
					continue;
				try {
					int n = int.Parse (dn);
					if (n >= lastDomainId)
						lastDomainId = n + 1;
				} catch {
				}
			}
			return lastDomainId.ToString ();
		}
		
		internal void ResetCachedData ()
		{
			allSetupInfos = null;
			addinSetupInfos = null;
			rootSetupInfos = null;
			hostIndex = null;
			cachedAddinSetupInfos.Clear ();
			if (addinEngine != null)
				addinEngine.ResetCachedData ();
		}
		
		
		public bool AddinDependsOn (string domain, string id1, string id2)
		{
			Hashtable visited = new Hashtable ();
			return AddinDependsOn (visited, domain, id1, id2);
		}
		
		bool AddinDependsOn (Hashtable visited, string domain, string id1, string id2)
		{
			if (visited.Contains (id1))
				return false;
			
			visited.Add (id1, id1);
			
			Addin addin1 = GetInstalledAddin (domain, id1, false);
			
			// We can assumbe that if the add-in is not returned here, it may be a root addin.
			if (addin1 == null)
				return false;

			id2 = Addin.GetIdName (id2);
			foreach (Dependency dep in addin1.AddinInfo.Dependencies) {
				AddinDependency adep = dep as AddinDependency;
				if (adep == null)
					continue;
				string depid = Addin.GetFullId (addin1.AddinInfo.Namespace, adep.AddinId, null);
				if (depid == id2)
					return true;
				else if (AddinDependsOn (visited, domain, depid, id2))
					return true;
			}
			return false;
		}
		
		public void Repair (IProgressStatus monitor, string domain)
		{
			using (fileDatabase.LockWrite ()) {
				try {
					if (Directory.Exists (AddinCachePath))
						Directory.Delete (AddinCachePath, true);
					if (Directory.Exists (AddinFolderCachePath))
						Directory.Delete (AddinFolderCachePath, true);
					if (File.Exists (HostIndexFile))
						File.Delete (HostIndexFile);
				}
				catch (Exception ex) {
					monitor.ReportError ("The add-in registry could not be rebuilt. It may be due to lack of write permissions to the directory: " + AddinDbDir, ex);
				}
			}
			Update (monitor, domain);
		}
		
		public void Update (IProgressStatus monitor, string domain)
		{
			if (monitor == null)
				monitor = new ConsoleProgressStatus (false);

			if (RunningSetupProcess)
				return;
			
			fatalDatabseError = false;
			
			DateTime tim = DateTime.Now;
			
			Hashtable installed = new Hashtable ();
			bool changesFound = CheckFolders (monitor, domain);
			
			if (monitor.IsCanceled)
				return;
			
			if (monitor.LogLevel > 1)
				monitor.Log ("Folders checked (" + (int) (DateTime.Now - tim).TotalMilliseconds + " ms)");
			
			if (changesFound) {
				// Something has changed, the add-ins need to be re-scanned, but it has
				// to be done in an external process
				
				if (domain != null) {
					using (fileDatabase.LockRead ()) {
						foreach (Addin ainfo in InternalGetInstalledAddins (domain, AddinSearchFlags.IncludeAddins)) {
							installed [ainfo.Id] = ainfo.Id;
						}
					}
				}
				
				RunScannerProcess (monitor);
			
				ResetCachedData ();
				
				registry.NotifyDatabaseUpdated ();
			}
			
			if (fatalDatabseError)
				monitor.ReportError ("The add-in database could not be updated. It may be due to file corruption. Try running the setup repair utility", null);
			
			// Update the currently loaded add-ins
			if (changesFound && domain != null && addinEngine != null && addinEngine.IsInitialized) {
				Hashtable newInstalled = new Hashtable ();
				foreach (Addin ainfo in GetInstalledAddins (domain, AddinSearchFlags.IncludeAddins)) {
					newInstalled [ainfo.Id] = ainfo.Id;
				}
				
				foreach (string aid in installed.Keys) {
					// Always try to unload, event if the add-in was not currently loaded.
					// Required since the add-ins has to be marked as 'disabled', to avoid
					// extensions from this add-in to be loaded
					if (!newInstalled.Contains (aid))
						addinEngine.UnloadAddin (aid);
				}
				
				foreach (string aid in newInstalled.Keys) {
					if (!installed.Contains (aid)) {
						Addin addin = addinEngine.Registry.GetAddin (aid);
						if (addin != null)
							addinEngine.ActivateAddin (aid);
					}
				}
			}
		}
		
		void RunScannerProcess (IProgressStatus monitor)
		{
			ISetupHandler setup = GetSetupHandler ();
			
			IProgressStatus scanMonitor = monitor;
			ArrayList pparams = new ArrayList ();
			
			bool retry = false;
			do {
				try {
					if (monitor.LogLevel > 1)
						monitor.Log ("Looking for addins");
					setup.Scan (scanMonitor, registry, null, (string[]) pparams.ToArray (typeof(string)));
					retry = false;
				}
				catch (Exception ex) {
					ProcessFailedException pex = ex as ProcessFailedException;
					if (pex != null) {
						// Get the last logged operation.
						if (pex.LastLog.StartsWith ("scan:")) {
							// It crashed while scanning a file. Add the file to the ignore list and try again.
							string file = pex.LastLog.Substring (5);
							pparams.Add (file);
							monitor.ReportWarning ("Could not scan file: " + file);
							retry = true;
							continue;
						}
					}
					fatalDatabseError = true;
					// If the process has crashed, try to do a new scan, this time using verbose log,
					// to give the user more information about the origin of the crash.
					if (pex != null && !retry) {
						monitor.ReportError ("Add-in scan operation failed. The Mono runtime may have encountered an error while trying to load an assembly.", null);
						if (monitor.LogLevel <= 1) {
							// Re-scan again using verbose log, to make it easy to find the origin of the error.
							retry = true;
							scanMonitor = new ConsoleProgressStatus (true);
						}
					} else
						retry = false;
					
					if (!retry) {
						monitor.ReportError ("Add-in scan operation failed", (ex is ProcessFailedException ? null : ex));
						monitor.Cancel ();
						return;
					}
				}
			}
			while (retry);
		}
		
		bool DatabaseInfrastructureCheck (IProgressStatus monitor)
		{
			// Do some sanity check, to make sure the basic database infrastructure can be created
			
			bool hasChanges = false;
			
			try {
			
				if (!Directory.Exists (AddinCachePath)) {
					Directory.CreateDirectory (AddinCachePath);
					hasChanges = true;
				}
			
				if (!Directory.Exists (AddinFolderCachePath)) {
					Directory.CreateDirectory (AddinFolderCachePath);
					hasChanges = true;
				}
			
				// Make sure we can write in those folders

				Util.CheckWrittableFloder (AddinCachePath);
				Util.CheckWrittableFloder (AddinFolderCachePath);
				
				fatalDatabseError = false;
			}
			catch (Exception ex) {
				monitor.ReportError ("Add-in cache directory could not be created", ex);
				fatalDatabseError = true;
				monitor.Cancel ();
			}
			return hasChanges;
		}
		
		
		internal bool CheckFolders (IProgressStatus monitor, string domain)
		{
			using (fileDatabase.LockRead ()) {
				AddinScanResult scanResult = new AddinScanResult ();
				scanResult.CheckOnly = true;
				scanResult.Domain = domain;
				InternalScanFolders (monitor, scanResult);
				return scanResult.ChangesFound;
			}
		}
		
		internal void ScanFolders (IProgressStatus monitor, string currentDomain, string folderToScan, StringCollection filesToIgnore)
		{
			AddinScanResult res = new AddinScanResult ();
			res.Domain = currentDomain;
			res.AddPathsToIgnore (filesToIgnore);
			ScanFolders (monitor, res);
		}
		
		void ScanFolders (IProgressStatus monitor, AddinScanResult scanResult)
		{
			IDisposable checkLock = null;
			
			if (scanResult.CheckOnly)
				checkLock = fileDatabase.LockRead ();
			else {
				// All changes are done in a transaction, which won't be committed until
				// all files have been updated.
				
				if (!fileDatabase.BeginTransaction ()) {
					// The database is already being updated. Can't do anything for now.
					return;
				}
			}
			
			EventInfo einfo = typeof(AppDomain).GetEvent ("ReflectionOnlyAssemblyResolve");
			ResolveEventHandler resolver = new ResolveEventHandler (OnResolveAddinAssembly);
			
			try
			{
				// Perform the add-in scan
				
				if (!scanResult.CheckOnly) {
					AppDomain.CurrentDomain.AssemblyResolve += resolver;
					if (einfo != null) einfo.AddEventHandler (AppDomain.CurrentDomain, resolver);
				}
				
				InternalScanFolders (monitor, scanResult);
				
				if (!scanResult.CheckOnly)
					fileDatabase.CommitTransaction ();
			}
			catch {
				if (!scanResult.CheckOnly)
					fileDatabase.RollbackTransaction ();
				throw;
			}
			finally {
				currentScanResult = null;
				
				if (scanResult.CheckOnly)
					checkLock.Dispose ();
				else {
					AppDomain.CurrentDomain.AssemblyResolve -= resolver;
					if (einfo != null) einfo.RemoveEventHandler (AppDomain.CurrentDomain, resolver);
				}
			}
		}
		
		void InternalScanFolders (IProgressStatus monitor, AddinScanResult scanResult)
		{
			try {
				fs.ScanStarted ();
				InternalScanFolders2 (monitor, scanResult);
			} finally {
				fs.ScanFinished ();
			}
		}
		
		void InternalScanFolders2 (IProgressStatus monitor, AddinScanResult scanResult)
		{
			DateTime tim = DateTime.Now;
			
			DatabaseInfrastructureCheck (monitor);
			if (monitor.IsCanceled)
				return;
			
			try {
				scanResult.HostIndex = GetAddinHostIndex ();
			}
			catch (Exception ex) {
				if (scanResult.CheckOnly) {
					scanResult.ChangesFound = true;
					return;
				}
				monitor.ReportError ("Add-in root index is corrupt. The add-in database will be regenerated.", ex);
				scanResult.RegenerateAllData = true;
			}
			
			AddinScanner scanner = new AddinScanner (this, scanResult, monitor);
			
			// Check if any of the previously scanned folders has been deleted
			
			foreach (string file in Directory.GetFiles (AddinFolderCachePath, "*.data")) {
				AddinScanFolderInfo folderInfo;
				bool res = ReadFolderInfo (monitor, file, out folderInfo);
				bool validForDomain = scanResult.Domain == null || folderInfo.Domain == GlobalDomain || folderInfo.Domain == scanResult.Domain;
				if (!res || (validForDomain && !fs.DirectoryExists (folderInfo.Folder))) {
					if (res) {
						// Folder has been deleted. Remove the add-ins it had.
						scanner.UpdateDeletedAddins (monitor, folderInfo, scanResult);
					}
					else {
						// Folder info file corrupt. Regenerate all.
						scanResult.ChangesFound = true;
						scanResult.RegenerateRelationData = true;
					}
					
					if (!scanResult.CheckOnly)
						SafeDelete (monitor, file);
					else if (scanResult.ChangesFound)
						return;
				}
			}
			
			// Look for changes in the add-in folders
			
			if (registry.StartupDirectory != null)
				scanner.ScanFolder (monitor, registry.StartupDirectory, null, scanResult);
			
			if (scanResult.CheckOnly && (scanResult.ChangesFound || monitor.IsCanceled))
				return;
			
			if (scanResult.Domain == null)
				scanner.ScanFolder (monitor, HostsPath, GlobalDomain, scanResult);
			
			if (scanResult.CheckOnly && (scanResult.ChangesFound || monitor.IsCanceled))
				return;
			
			foreach (string dir in registry.GlobalAddinDirectories) {
				if (scanResult.CheckOnly && (scanResult.ChangesFound || monitor.IsCanceled))
					return;
				scanner.ScanFolderRec (monitor, dir, GlobalDomain, scanResult);
			}
			
			if (scanResult.CheckOnly || !scanResult.ChangesFound)
				return;
			
			// Scan the files which have been modified
			
			currentScanResult = scanResult;

			foreach (FileToScan file in scanResult.FilesToScan)
				scanner.ScanFile (monitor, file.File, file.AddinScanFolderInfo, scanResult);

			// Save folder info
			
			foreach (AddinScanFolderInfo finfo in scanResult.ModifiedFolderInfos)
				SaveFolderInfo (monitor, finfo);

			if (monitor.LogLevel > 1)
				monitor.Log ("Folders scan completed (" + (int) (DateTime.Now - tim).TotalMilliseconds + " ms)");

			SaveAddinHostIndex ();
			ResetCachedData ();
			
			if (!scanResult.ChangesFound) {
				if (monitor.LogLevel > 1)
					monitor.Log ("No changes found");
				return;
			}
			
			tim = DateTime.Now;
			try {
				if (scanResult.RegenerateRelationData) {
					if (monitor.LogLevel > 1)
						monitor.Log ("Regenerating all add-in relations.");
					scanResult.AddinsToUpdate = null;
					scanResult.AddinsToUpdateRelations = null;
				}
				
				GenerateAddinExtensionMapsInternal (monitor, scanResult.AddinsToUpdate, scanResult.AddinsToUpdateRelations, scanResult.RemovedAddins);
			}
			catch (Exception ex) {
				fatalDatabseError = true;
				monitor.ReportError ("The add-in database could not be updated. It may be due to file corruption. Try running the setup repair utility", ex);
			}
			
			if (monitor.LogLevel > 1)
				monitor.Log ("Add-in relations analyzed (" + (int) (DateTime.Now - tim).TotalMilliseconds + " ms)");
			
			SaveAddinHostIndex ();
		}
		
		public void ParseAddin (IProgressStatus progressStatus, string domain, string file, string outFile, bool inProcess)
		{
			if (!inProcess) {
				ISetupHandler setup = GetSetupHandler ();
				setup.GetAddinDescription (progressStatus, registry, Path.GetFullPath (file), outFile);
				return;
			}
			
			using (fileDatabase.LockRead ())
			{
				// First of all, check if the file belongs to a registered add-in
				AddinScanFolderInfo finfo;
				if (GetFolderInfoForPath (progressStatus, Path.GetDirectoryName (file), out finfo) && finfo != null) {
					AddinFileInfo afi = finfo.GetAddinFileInfo (file);
					if (afi != null && afi.IsAddin) {
						AddinDescription adesc;
						GetAddinDescription (progressStatus, afi.Domain, afi.AddinId, out adesc);
						if (adesc != null)
							adesc.Save (outFile);
						return;
					}
				}
				
				AddinScanResult sr = new AddinScanResult ();
				sr.Domain = domain;
				AddinScanner scanner = new AddinScanner (this, sr, progressStatus);
				
				SingleFileAssemblyResolver res = new SingleFileAssemblyResolver (progressStatus, registry, scanner);
				ResolveEventHandler resolver = new ResolveEventHandler (res.Resolve);

				EventInfo einfo = typeof(AppDomain).GetEvent ("ReflectionOnlyAssemblyResolve");
				
				try {
					AppDomain.CurrentDomain.AssemblyResolve += resolver;
					if (einfo != null) einfo.AddEventHandler (AppDomain.CurrentDomain, resolver);
				
					AddinDescription desc = scanner.ScanSingleFile (progressStatus, file, sr);
					if (desc != null)
						desc.Save (outFile);
				}
				finally {
					AppDomain.CurrentDomain.AssemblyResolve -= resolver;
					if (einfo != null) einfo.RemoveEventHandler (AppDomain.CurrentDomain, resolver);
				}
			}
		}
		
		public string GetFolderDomain (IProgressStatus progressStatus, string path)
		{
			AddinScanFolderInfo folderInfo;
			if (GetFolderInfoForPath (progressStatus, path, out folderInfo) && folderInfo != null && !folderInfo.SharedFolder)
				return folderInfo.Domain;
			else
				return UnknownDomain;
		}
		
		Assembly OnResolveAddinAssembly (object s, ResolveEventArgs args)
		{
			string file = currentScanResult != null ? currentScanResult.GetAssemblyLocation (args.Name) : null;
			if (file != null)
				return Util.LoadAssemblyForReflection (file);
			else {
				if (!args.Name.StartsWith ("Mono.Addins.CecilReflector"))
					Console.WriteLine ("Assembly not found: " + args.Name);
				return null;
			}
		}
		
		public string GetFolderConfigFile (string path)
		{
			path = Path.GetFullPath (path);
			
			string s = path.Replace ("_", "__");
			s = s.Replace (Path.DirectorySeparatorChar, '_');
			s = s.Replace (Path.AltDirectorySeparatorChar, '_');
			s = s.Replace (Path.VolumeSeparatorChar, '_');
			
			return Path.Combine (AddinFolderCachePath, s + ".data");
		}
		
		internal void UninstallAddin (IProgressStatus monitor, string domain, string addinId, AddinScanResult scanResult)
		{
			scanResult.AddRemovedAddin (addinId);
			string file = GetDescriptionPath (domain, addinId);
			if (!fileDatabase.Exists (file)) {
				return;
			}
			
			// Add-in already existed. The dependencies of the old add-in need to be re-analized

			AddinDescription desc;
			if (ReadAddinDescription (monitor, file, out desc)) {
				Util.AddDependencies (desc, scanResult);
				if (desc.IsRoot)
					scanResult.HostIndex.RemoveHostData (desc.AddinId, desc.AddinFile);
			} else
				// If we can't get information about the old assembly, just regenerate all relation data
				scanResult.RegenerateRelationData = true;

			SafeDelete (monitor, file);
			string dir = Path.GetDirectoryName (file);
			if (fileDatabase.DirectoryIsEmpty (dir))
				SafeDeleteDir (monitor, dir);
			SafeDeleteDir (monitor, Path.Combine (AddinPrivateDataPath, Path.GetFileNameWithoutExtension (file)));
		}
		
		public bool GetAddinDescription (IProgressStatus monitor, string domain, string addinId, out AddinDescription description)
		{
			string file = GetDescriptionPath (domain, addinId);
			return ReadAddinDescription (monitor, file, out description);
		}
		
		public bool ReadAddinDescription (IProgressStatus monitor, string file, out AddinDescription description)
		{
			try {
				description = AddinDescription.ReadBinary (fileDatabase, file);
				if (description != null)
					description.OwnerDatabase = this;
				return true;
			}
			catch (Exception ex) {
				if (monitor == null)
					throw;
				description = null;
				monitor.ReportError ("Could not read folder info file", ex);
				return false;
			}
		}
		
		public bool SaveDescription (IProgressStatus monitor, AddinDescription desc, string replaceFileName)
		{
			try {
				if (replaceFileName != null)
					desc.SaveBinary (fileDatabase, replaceFileName);
				else {
					string file = GetDescriptionPath (desc.Domain, desc.AddinId);
					string dir = Path.GetDirectoryName (file);
					if (!fileDatabase.DirExists (dir))
						fileDatabase.CreateDir (dir);
					desc.SaveBinary (fileDatabase, file);
				}
				return true;
			}
			catch (Exception ex) {
				monitor.ReportError ("Add-in info file could not be saved", ex);
				return false;
			}
		}
		
		public bool AddinDescriptionExists (string domain, string addinId)
		{
			string file = GetDescriptionPath (domain, addinId);
			return fileDatabase.Exists (file);
		}
		
		public bool ReadFolderInfo (IProgressStatus monitor, string file, out AddinScanFolderInfo folderInfo)
		{
			try {
				folderInfo = AddinScanFolderInfo.Read (fileDatabase, file);
				return true;
			}
			catch (Exception ex) {
				folderInfo = null;
				monitor.ReportError ("Could not read folder info file", ex);
				return false;
			}
		}
		
		public bool GetFolderInfoForPath (IProgressStatus monitor, string path, out AddinScanFolderInfo folderInfo)
		{
			try {
				folderInfo = AddinScanFolderInfo.Read (fileDatabase, AddinFolderCachePath, path);
				return true;
			}
			catch (Exception ex) {
				folderInfo = null;
				if (monitor != null)
					monitor.ReportError ("Could not read folder info file", ex);
				return false;
			}
		}

		public bool SaveFolderInfo (IProgressStatus monitor, AddinScanFolderInfo folderInfo)
		{
			try {
				folderInfo.Write (fileDatabase, AddinFolderCachePath);
				return true;
			}
			catch (Exception ex) {
				monitor.ReportError ("Could not write folder info file", ex);
				return false;
			}
		}
		
		public bool DeleteFolderInfo (IProgressStatus monitor, AddinScanFolderInfo folderInfo)
		{
			return SafeDelete (monitor, folderInfo.FileName);
		}
		
		public bool SafeDelete (IProgressStatus monitor, string file)
		{
			try {
				fileDatabase.Delete (file);
				return true;
			}
			catch (Exception ex) {
				if (monitor.LogLevel > 1) {
					monitor.Log ("Could not delete file: " + file);
					monitor.Log (ex.ToString ());
				}
				return false;
			}
		}
		
		public bool SafeDeleteDir (IProgressStatus monitor, string dir)
		{
			try {
				fileDatabase.DeleteDir (dir);
				return true;
			}
			catch (Exception ex) {
				if (monitor.LogLevel > 1) {
					monitor.Log ("Could not delete directory: " + dir);
					monitor.Log (ex.ToString ());
				}
				return false;
			}
		}
		
		AddinHostIndex GetAddinHostIndex ()
		{
			if (hostIndex != null)
				return hostIndex;
			
			using (fileDatabase.LockRead ()) {
				if (fileDatabase.Exists (HostIndexFile))
					hostIndex = AddinHostIndex.Read (fileDatabase, HostIndexFile);
				else
					hostIndex = new AddinHostIndex ();
			}
			return hostIndex;
		}
		
		void SaveAddinHostIndex ()
		{
			if (hostIndex != null)
				hostIndex.Write (fileDatabase, HostIndexFile);
		}
		
		internal string GetUniqueAddinId (string file, string oldId, string ns, string version)
		{
			string baseId = "__" + Path.GetFileNameWithoutExtension (file);

			if (Path.GetExtension (baseId) == ".addin")
				baseId = Path.GetFileNameWithoutExtension (baseId);
			
			string name = baseId;
			string id = Addin.GetFullId (ns, name, version);
			
			// If the old Id is already an automatically generated one, reuse it
			if (oldId != null && oldId.StartsWith (id))
				return name;
			
			int n = 1;
			while (AddinIdExists (id)) {
				name = baseId + "_" + n;
				id = Addin.GetFullId (ns, name, version);
				n++;
			}
			return name;
		}
		
		bool AddinIdExists (string id)
		{
			foreach (string d in fileDatabase.GetDirectories (AddinCachePath)) {
				if (fileDatabase.Exists (Path.Combine (d, id + ".addin")))
				    return true;
			}
			return false;
		}
		
		ISetupHandler GetSetupHandler ()
		{
//			if (Util.IsMono)
//				return new SetupProcess ();
//			else
			if (fs.RequiresIsolation)
				return new SetupDomain ();
			else
				return new SetupLocal ();
		}
		
		public void ResetConfiguration ()
		{
			if (File.Exists (ConfigFile))
				File.Delete (ConfigFile);
		}
		
		DatabaseConfiguration Configuration {
			get {
				if (config == null) {
					using (fileDatabase.LockRead ()) {
						if (fileDatabase.Exists (ConfigFile))
							config = DatabaseConfiguration.Read (ConfigFile);
						else
							config = new DatabaseConfiguration ();
					}
				}
				return config;
			}
		}
		
		void SaveConfiguration ()
		{
			if (config != null) {
				using (fileDatabase.LockWrite ()) {
					config.Write (ConfigFile);
				}
			}
		}
	}
	
	class SingleFileAssemblyResolver
	{
		AddinScanResult scanResult;
		AddinScanner scanner;
		AddinRegistry registry;
		IProgressStatus progressStatus;
		
		public SingleFileAssemblyResolver (IProgressStatus progressStatus, AddinRegistry registry, AddinScanner scanner)
		{
			this.scanner = scanner;
			this.registry = registry;
			this.progressStatus = progressStatus;
		}
		
		public Assembly Resolve (object s, ResolveEventArgs args)
		{
			if (scanResult == null) {
				scanResult = new AddinScanResult ();
				scanResult.LocateAssembliesOnly = true;
			
				if (registry.StartupDirectory != null)
					scanner.ScanFolder (progressStatus, registry.StartupDirectory, null, scanResult);
				foreach (string dir in registry.GlobalAddinDirectories)
					scanner.ScanFolderRec (progressStatus, dir, AddinDatabase.GlobalDomain, scanResult);
			}
		
			string afile = scanResult.GetAssemblyLocation (args.Name);
			if (afile != null)
				return Util.LoadAssemblyForReflection (afile);
			else
				return null;
		}
	}
	
	class AddinIndex
	{
		Dictionary<string, List<AddinDescription>> addins = new Dictionary<string, List<AddinDescription>> ();
		
		public void Add (AddinDescription desc)
		{
			string id = Addin.GetFullId (desc.Namespace, desc.LocalId, null);
			List<AddinDescription> list;
			if (!addins.TryGetValue (id, out list))
				addins [id] = list = new List<AddinDescription> ();
			list.Add (desc);
		}
		
		List<AddinDescription> FindDescriptions (string domain, string fullid)
		{
			// Returns all registered add-ins which are compatible with the provided
			// fullid. Compatible means that the id is the same and the version is within
			// the range of compatible versions of the add-in.
			
			var res = new List<AddinDescription> ();
			string id = Addin.GetIdName (fullid);
			List<AddinDescription> list;
			if (!addins.TryGetValue (id, out list))
				return res;
			string version = Addin.GetIdVersion (fullid);
			foreach (AddinDescription desc in list) {
				if ((desc.Domain == domain || domain == AddinDatabase.GlobalDomain) && desc.SupportsVersion (version))
					res.Add (desc);
			}
			return res;
		}
		
		public IEnumerable<string> GetMissingDependencies (AddinDescription desc, ModuleDescription mod)
		{
			foreach (Dependency dep in mod.Dependencies) {
				AddinDependency adep = dep as AddinDependency;
				if (adep == null)
					continue;
				var descs = FindDescriptions (desc.Domain, adep.FullAddinId);
				if (descs.Count == 0)
					yield return adep.FullAddinId;
			}
		}
		
		public AddinDescription GetSimilarExistingAddin (AddinDescription conf, string addinId)
		{
			string domain = conf.Domain;
			List<AddinDescription> list;
			if (!addins.TryGetValue (Addin.GetIdName (addinId), out list))
				return null;
			string version = Addin.GetIdVersion (addinId);
			foreach (AddinDescription desc in list) {
				if ((desc.Domain == domain || domain == AddinDatabase.GlobalDomain) && !desc.SupportsVersion (version))
					return desc;
			}
			return null;
		}
		
		public List<AddinDescription> GetSortedAddins ()
		{
			var inserted = new HashSet<string> ();
			var lists = new Dictionary<string,List<AddinDescription>> ();
			
			foreach (List<AddinDescription> dlist in addins.Values) {
				foreach (AddinDescription desc in dlist)
					InsertSortedAddin (inserted, lists, desc);
			}
			
			// Merge all domain lists into a single list.
			// Make sure the global domain is inserted the last
			
			List<AddinDescription> global;
			lists.TryGetValue (AddinDatabase.GlobalDomain, out global);
			lists.Remove (AddinDatabase.GlobalDomain);
			
			List<AddinDescription> sortedAddins = new List<AddinDescription> ();
			foreach (var dl in lists.Values) {
				sortedAddins.AddRange (dl);
			}
			if (global != null)
				sortedAddins.AddRange (global);
			return sortedAddins;
		}

		void InsertSortedAddin (HashSet<string> inserted, Dictionary<string,List<AddinDescription>> lists, AddinDescription desc)
		{
			string sid = desc.AddinId + " " + desc.Domain;
			if (!inserted.Add (sid))
				return;

			foreach (ModuleDescription mod in desc.AllModules) {
				foreach (Dependency dep in mod.Dependencies) {
					AddinDependency adep = dep as AddinDependency;
					if (adep == null)
						continue;
					var descs = FindDescriptions (desc.Domain, adep.FullAddinId);
					if (descs.Count > 0) {
						foreach (AddinDescription sd in descs)
							InsertSortedAddin (inserted, lists, sd);
					}
				}
			}
			List<AddinDescription> list;
			if (!lists.TryGetValue (desc.Domain, out list))
				lists [desc.Domain] = list = new List<AddinDescription> ();
			
			list.Add (desc);
		}
	}
}


