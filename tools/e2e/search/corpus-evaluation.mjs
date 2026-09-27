import { publicObservation } from './corpus.mjs';

function text(value,name,{nullable=false}={}) {
  if(nullable&&value==null)return null;
  if(typeof value!=='string'||!value.trim())throw new TypeError(`${name} must be a nonempty string`);
  return value;
}
function stringArray(value,name){if(!Array.isArray(value))throw new TypeError(`${name} must be an array`);return value.map(v=>text(v,name));}
function date(value,name){text(value,name);if(!Number.isFinite(Date.parse(value)))throw new TypeError(`${name} must be a date`);return value;}
function positive(value,name,{zero=false}={}){if(!Number.isSafeInteger(value)||value<(zero?0:1))throw new TypeError(`${name} must be a ${zero?'nonnegative':'positive'} integer`);return value;}
function projection(value,scalarFields,arrayFields=[]){
  const result={};
  for(const field of scalarFields)if(value[field]!==undefined){if(value[field]!==null&&!['string','boolean','number'].includes(typeof value[field]))throw new TypeError(`${field} must be scalar`);result[field]=value[field];}
  for(const field of arrayFields)if(value[field]!==undefined)result[field]=value[field]===null?null:stringArray(value[field],field);
  return result;
}
function publicMetadata(metadata){
  const out=projection(metadata,['id','externalId','title','sport','season','eventDate','broadcastDate','homeTeamName','awayTeamName','round','monitored']);
  text(out.id,'metadata.id');text(out.title,'metadata.title');date(out.eventDate,'metadata.eventDate');if(out.broadcastDate)date(out.broadcastDate,'metadata.broadcastDate');
  for(const [key,scalars,arrays]of [['league',['name'],['alternateNames']],['homeTeam',['name'],['aliases']],['awayTeam',['name'],['aliases']]]){
    if(metadata[key]!==undefined)out[key]=metadata[key]===null?null:projection(metadata[key],scalars,arrays);
  }
  return out;
}
function indexCorpus(corpus,caseId){
  if(!corpus||!Array.isArray(corpus.cases)||!Array.isArray(corpus.inventories))throw new TypeError('corpus cases and inventories are required');
  const matches=corpus.cases.filter(c=>c.id===caseId);if(matches.length!==1)throw new Error(matches.length?'Duplicate case ID':'Unknown case '+caseId);
  const target=matches[0],byGuid=new Map();
  for(const c of corpus.cases){if(!Array.isArray(c.observations))throw new TypeError('case observations are required');for(const o of c.observations){text(o.guid,'observation.guid');if(byGuid.has(o.guid))throw new Error('Duplicate observation GUID '+o.guid);byGuid.set(o.guid,o);}}
  const inventories=corpus.inventories.filter(i=>i.id===target.source?.inventoryId);
  if(inventories.length!==1)throw new Error('Exactly one shared inventory is required');
  const inventory=inventories[0];if(!Array.isArray(inventory.releaseGuids)||new Set(inventory.releaseGuids).size!==inventory.releaseGuids.length)throw new Error('Inventory GUIDs must be a unique array');
  const observations=inventory.releaseGuids.map(guid=>{if(!byGuid.has(guid))throw new Error('Unknown inventory GUID '+guid);return byGuid.get(guid);});
  return {target,byGuid,inventory,observations};
}
function publicRelease(observation){
  const out=publicObservation(observation);
  text(out.guid,'guid');text(out.title,'title');date(out.publishDate,'publishDate');positive(out.category,'category');positive(out.size,'size',{zero:true});positive(out.seeders,'seeders',{zero:true});
  if(!['Torrent','Usenet'].includes(out.protocol))throw new TypeError('protocol must be Torrent or Usenet');
  return out;
}
export function prepareRetrievalCase(corpus,caseId){
  const {target,observations}=indexCorpus(corpus,caseId);
  if(!['and','phrase','ordered'].includes(target.source.model))throw new TypeError('Unknown source search model');
  const profile=projection(target.profile??{},['name','languages','allowHighlights','minSeeders','monitored'],['allowedQualities']);
  return {metadata:publicMetadata(target.metadata),requestedPart:text(target.requestedPart,'requestedPart',{nullable:true}),profile,
    source:{searchMode:target.source.model,pageSize:positive(target.source.pageSize,'pageSize'),category:positive(target.source.category,'category'),quota:positive(target.source.quota,'quota'),releases:observations.map(publicRelease)}};
}
function coversTarget(observation,target){
  if(!observation.coverage||!Array.isArray(observation.coverage.members))throw new TypeError('Coverage members are required for '+observation.guid);
  text(target.truth?.eventId,'target semantic identity');
  return ['complete','pack'].includes(observation.coverage.kind)&&observation.coverage.members.some(member=>member.eventId===target.truth.eventId&&(member.part??null)===(target.requestedPart??null));
}
function requestMetrics(requests){
  if(requests==null)return {sourceRequestCount:null,queryCount:null,distinctQueryCount:null,feedRequestCount:null,rateLimitedRequests:null};
  if(!Array.isArray(requests))throw new TypeError('sourceRequests must be an array or null');
  let queryCount=0,feedRequestCount=0,rateLimitedRequests=0;const queries=new Set();
  const criteria=['q','sportarrid','tvdbid','tvmazeid','imdbid','tmdbid','rid'];
  for(const request of requests){
    if(!request||typeof request!=='object'||!request.query||typeof request.query!=='object'||Array.isArray(request.query))throw new TypeError('source request requires parsed query parameters');
    for(const value of Object.values(request.query))if(value!==null&&!['string','number','boolean'].includes(typeof value))throw new TypeError('source request query values must be scalar');
    const params=request.query;
    if(Number(request.status)===429)rateLimitedRequests++;
    if(!['search','tvsearch','tv-search'].includes(params.t))continue;
    if(!criteria.some(key=>params[key]!==undefined&&params[key]!==null&&String(params[key]).trim()!=='')){feedRequestCount++;continue;}
    queryCount++;
    queries.add(JSON.stringify(Object.entries(params).filter(([key])=>!['apikey','offset','limit','extended'].includes(key)).sort(([a],[b])=>a.localeCompare(b))));
  }
  return {sourceRequestCount:requests.length,queryCount,distinctQueryCount:queries.size,feedRequestCount,rateLimitedRequests};
}
function approval(rows,field){
  if(rows.some(row=>row[field]===true))return true;
  return rows.every(row=>row[field]===false)?false:null;
}
export function gradeRetrieval(corpus,caseId,{returnedRows,sourceRequests}={}){
  if(!Array.isArray(returnedRows))throw new TypeError('returnedRows must be an array');
  const {target,byGuid,observations}=indexCorpus(corpus,caseId);
  const eligibleGuids=new Set(observations.map(o=>o.guid));
  const relevantGuids=new Set(observations.filter(o=>coversTarget(o,target)).map(o=>o.guid));
  const groups=new Map(),errors=[];
  for(const row of returnedRows){
    if(!row||typeof row!=='object')throw new TypeError('Each returned row must be an object');
    text(row.guid,'returned row guid');
    for(const field of ['approved','autoApproved'])if(row[field]!=null&&typeof row[field]!=='boolean')throw new TypeError(`${field} must be boolean or null`);
    if(!byGuid.has(row.guid)||!eligibleGuids.has(row.guid))errors.push({code:byGuid.has(row.guid)?'OUT_OF_INVENTORY_GUID':'UNKNOWN_GUID',guid:row.guid});
    const existing=groups.get(row.guid)??[];existing.push(row);groups.set(row.guid,existing);
  }
  const classified=[...groups.entries()].filter(([guid])=>eligibleGuids.has(guid)).map(([guid,rows])=>({guid,relevant:relevantGuids.has(guid),approved:approval(rows,'approved'),autoApproved:approval(rows,'autoApproved')}));
  const relevantReturnedGuids=classified.filter(r=>r.relevant).map(r=>r.guid).sort();
  const incorrect=classified.filter(r=>!r.relevant);
  const ambiguous=target.metadataConfidence!=='complete'||['userAutomatic','unattended','rss'].some(route=>target.expectedPolicies?.[route]?.decision==='abstain');
  const unknown=errors.length>0;
  const metric=(rows,field)=>unknown||rows.some(r=>r[field]===null)?null:rows.filter(r=>r[field]).length;
  const approvedFalsePositives=metric(incorrect,'approved');
  const automaticFalsePositives=metric(incorrect,'autoApproved');
  const ambiguousApprovedExposure=ambiguous?metric(classified,'approved'):0;
  const ambiguousAutoApprovalRisk=ambiguous?metric(classified,'autoApproved'):0;
  const counts=requestMetrics(sourceRequests);
  const availability=relevantGuids.size>0;
  const incomplete=counts.queryCount===null||approvedFalsePositives===null||automaticFalsePositives===null||(ambiguous&&(ambiguousAutoApprovalRisk===null||ambiguousApprovedExposure===null));
  return {caseId,scope:'Synthetic shared-inventory retrieval and approval evidence only',measurementStatus:unknown?'INVALID':incomplete?'INCOMPLETE':'COMPLETE',
    result:unknown?'UNCLASSIFIED':!availability?'NO_RELEVANT_AVAILABLE':relevantReturnedGuids.length?'RETRIEVED':'MISSED',
    targetSemanticEventId:target.truth.eventId,declaredKnownAvailable:target.knownAvailable,inventoryKnownAvailable:availability,availabilityLabelDisagrees:target.knownAvailable!==availability,
    availableRelevantOffers:relevantGuids.size,returnedRows:returnedRows.length,returnedUniqueOffers:groups.size,duplicateReturnedRows:returnedRows.length-groups.size,
    returnedRelevantOffers:relevantReturnedGuids.length,relevantRecall:unknown||!availability?null:relevantReturnedGuids.length/relevantGuids.size,
    relevantReturnedGuids,missedRelevantGuids:[...relevantGuids].filter(guid=>!groups.has(guid)).sort(),
    automaticFalsePositives,automaticFalsePositiveGuids:incorrect.filter(r=>r.autoApproved===true).map(r=>r.guid).sort(),
    approvedFalsePositives,approvedFalsePositiveGuids:incorrect.filter(r=>r.approved===true).map(r=>r.guid).sort(),ambiguousTarget:ambiguous,
    ambiguousApprovedExposure,ambiguousAutoApprovalRisk,unclassifiedReturnedRows:errors.length,errors,...counts,
    transfer:{status:'UNMEASURED',clientAdds:null,bytesDownloaded:null,filesImported:null,payloadHashVerified:null}};
}
