import test from 'node:test';
import assert from 'node:assert/strict';
import {createCorpus} from './corpus.mjs';
import {createHash} from 'node:crypto';
import {prepareRetrievalCase,gradeRetrieval} from './corpus-evaluation.mjs';
const accepted=createCorpus();
const query={method:'GET',path:'/api',query:{t:'search',q:'Example'},status:200};
function specimen(){
 const content=(eventId,part=null)=>({eventId,part});
 const observation=(guid,members,kind='complete',label=false)=>({guid,title:`League Fixture ${guid} 720p WEB-DL`,publishDate:'2024-01-02T00:00:00.000Z',category:5060,size:1234,seeders:10,protocol:'Torrent',coverage:{kind,members},label:{relevant:label},payload:{secret:'hidden'}});
 const target={id:'assignment-target',metadata:{id:'assignment-target',title:'Fixture',eventDate:'2024-01-01T00:00:00.000Z',sport:'Fighting',league:{name:'League'},secret:'do not expose'},truth:{eventId:'semantic-target'},requestedPart:'Main Card',knownAvailable:true,metadataConfidence:'complete',profile:{name:'Fixture',allowedQualities:['WEBDL-720p'],languages:'any',allowHighlights:false,minSeeders:1,monitored:true},source:{inventoryId:'shared',model:'and',pageSize:5,category:5060,quota:20},expectedPolicies:{userAutomatic:{decision:'grab'},unattended:{decision:'grab'},rss:{decision:'grab'}},observations:[observation('own',[content('semantic-target','Main Card')],'complete',true)]};
 const other={id:'assignment-other',observations:[observation('cross-pack',[content('semantic-other'),content('semantic-target','Main Card')],'pack',false),observation('excluded-pack',[content('semantic-other')],'pack',true),observation('wrong-part',[content('semantic-target','Prelims')],'complete',true),observation('clip',[content('semantic-target','Main Card')],'Highlights',true)]};
 return {cases:[target,other],inventories:[{id:'shared',releaseGuids:['clip','cross-pack','own','wrong-part','excluded-pack']}]};
}

test('preparation exposes full accepted shared inventory without private labels',()=>{
 const c=accepted.cases[0],p=prepareRetrievalCase(accepted,c.id);
 assert.equal(p.source.releases.length,accepted.inventories.find(i=>i.id===c.source.inventoryId).releaseGuids.length);
 assert.ok(p.source.releases.length>=1280);
 assert.deepEqual(Object.keys(p.source.releases[0]),['guid','title','publishDate','category','size','seeders','protocol']);
 const json=JSON.stringify(p.source);for(const key of ['knownAvailable','semantic-','contentIdentity','coverage','payload','allowedAssignments','label','metadataConfidence','split'])assert.ok(!json.includes(key),key);
 const original=specimen(),small=prepareRetrievalCase(original,'assignment-target');assert.ok(!JSON.stringify(small).includes('do not expose'));
 small.metadata.league.name='changed';assert.equal(original.cases[0].metadata.league.name,'League');
});

test('shared-inventory relevance follows coverage, not the owning case label',()=>{
 const report=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'cross-pack',approved:true,autoApproved:true},{guid:'excluded-pack',approved:true,autoApproved:false}],sourceRequests:[query]});
 assert.equal(report.availableRelevantOffers,2);assert.equal(report.returnedRelevantOffers,1);assert.equal(report.relevantRecall,0.5);assert.equal(report.approvedFalsePositives,1);
 assert.deepEqual(report.relevantReturnedGuids,['cross-pack']);assert.equal(report.queryCount,1);
});

test('wrong parts and incomplete clips cannot satisfy full target coverage',()=>{
 const report=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'wrong-part',approved:true},{guid:'clip',approved:true}],sourceRequests:[]});
 assert.equal(report.relevantRecall,0);assert.equal(report.approvedFalsePositives,2);assert.equal(report.transfer.status,'UNMEASURED');assert.equal(report.transfer.filesImported,null);
});

test('ambiguous targets distinguish listed approval exposure from actual automatic approval',()=>{
 const c=specimen();c.cases[0].metadataConfidence='incorrect';c.cases[0].expectedPolicies.userAutomatic.decision='abstain';
 const report=gradeRetrieval(c,'assignment-target',{returnedRows:[{guid:'own',approved:true}],sourceRequests:[query]});
 assert.equal(report.ambiguousApprovedExposure,1);assert.equal(report.ambiguousAutoApprovalRisk,null);
 const actual=gradeRetrieval(c,'assignment-target',{returnedRows:[{guid:'own',approved:true,autoApproved:true}],sourceRequests:[query]});
 assert.equal(actual.ambiguousAutoApprovalRisk,1);
});

test('no available content and empty returned results preserve undefined recall',()=>{
 const c=specimen();c.inventories[0].releaseGuids=['clip','wrong-part'];c.cases[0].knownAvailable=false;c.cases[0].metadataConfidence='partial';c.cases[0].expectedPolicies.userAutomatic.decision='abstain';
 const report=gradeRetrieval(c,'assignment-target',{returnedRows:[],sourceRequests:[]});
 assert.equal(report.availableRelevantOffers,0);assert.equal(report.relevantRecall,null);assert.equal(report.approvedFalsePositives,0);assert.equal(report.ambiguousAutoApprovalRisk,0);
 assert.equal(report.result,'NO_RELEVANT_AVAILABLE');
});

test('empty query results count a retrieval miss without inventing a transfer',()=>{
 const report=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[],sourceRequests:[query]});
 assert.equal(report.relevantRecall,0);assert.equal(report.result,'MISSED');assert.equal(report.queryCount,1);assert.equal(report.transfer.status,'UNMEASURED');
});

test('unknown or out-of-inventory GUIDs are unclassified errors',()=>{
 const c=specimen();c.inventories[0].releaseGuids=c.inventories[0].releaseGuids.filter(g=>g!=='clip');
 const report=gradeRetrieval(c,'assignment-target',{returnedRows:[{guid:'unknown',approved:true},{guid:'clip',approved:true}],sourceRequests:[]});
 assert.equal(report.measurementStatus,'INVALID');assert.equal(report.unclassifiedReturnedRows,2);assert.equal(report.approvedFalsePositives,null);assert.equal(report.errors.length,2);
});

test('missing approval evidence stays unmeasured and duplicates do not inflate recall',()=>{
 const report=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'own',approved:true},{guid:'own',approved:false},{guid:'wrong-part'}]});
 assert.equal(report.returnedRelevantOffers,1);assert.equal(report.duplicateReturnedRows,1);assert.equal(report.approvedFalsePositives,null);assert.equal(report.queryCount,null);
});

test('source accounting separates capabilities, feeds, queries and pagination',()=>{
 const requests=[{...query,query:{t:'caps'}},{...query,query:{t:'search'}},query,{...query,query:{...query.query,offset:'5'}},{...query,query:{t:'tvsearch',q:'Other'},status:429}];
 const report=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[],sourceRequests:requests});
 assert.equal(report.sourceRequestCount,5);assert.equal(report.queryCount,3);assert.equal(report.distinctQueryCount,2);assert.equal(report.feedRequestCount,1);assert.equal(report.rateLimitedRequests,1);
});

test('malformed public scalars and duplicate catalogue identities fail preparation',()=>{
 const c=specimen();c.cases[0].observations[0].title={label:'hidden'};assert.throws(()=>prepareRetrievalCase(c,'assignment-target'),/title/);
 const duplicate=specimen();duplicate.cases[1].observations.push({...duplicate.cases[0].observations[0]});assert.throws(()=>prepareRetrievalCase(duplicate,'assignment-target'),/duplicate.*GUID/i);
 assert.throws(()=>prepareRetrievalCase(specimen(),'missing'),/Unknown case/);
 assert.throws(()=>gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'own',approved:'true'}]}),/approved/);
});

test('the reference input remains the exact independently accepted revision',()=>{
 const bytes=Buffer.from(JSON.stringify(accepted));
 assert.equal(createHash('sha256').update(bytes).digest('hex'),'4568ed45ed9baf5f96b7fde42d3c31ed1e4bce35c93c57b43ac60bf9756cd6ea');
});

test('another case pack can make an otherwise unavailable target available',()=>{
 const c=specimen();c.cases[0].knownAvailable=false;c.inventories[0].releaseGuids=['cross-pack','excluded-pack'];
 const r=gradeRetrieval(c,'assignment-target',{returnedRows:[{guid:'cross-pack',approved:true}],sourceRequests:[]});
 assert.equal(r.availableRelevantOffers,1);assert.equal(r.relevantRecall,1);assert.equal(r.declaredKnownAvailable,false);assert.equal(r.inventoryKnownAvailable,true);assert.equal(r.availabilityLabelDisagrees,true);
});

test('wrong automatic approvals remain visible independently of generic approval',()=>{
 const result=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'wrong-part',approved:false,autoApproved:true}],sourceRequests:[]});
 assert.equal(result.approvedFalsePositives,0);
 assert.equal(result.automaticFalsePositives,1);
 assert.deepEqual(result.automaticFalsePositiveGuids,['wrong-part']);
 const duplicate=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'wrong-part',approved:false,autoApproved:false},{guid:'wrong-part',autoApproved:true}],sourceRequests:[]});
 assert.equal(duplicate.automaticFalsePositives,1);
 assert.deepEqual(duplicate.automaticFalsePositiveGuids,['wrong-part']);
 const missing=gradeRetrieval(specimen(),'assignment-target',{returnedRows:[{guid:'wrong-part',approved:false}],sourceRequests:[]});
 assert.equal(missing.automaticFalsePositives,null);
 assert.equal(missing.measurementStatus,'INCOMPLETE');
});

test('ambiguous generic approval evidence cannot be inferred from automatic evidence',()=>{
 const c=specimen();c.cases[0].metadataConfidence='incorrect';
 const result=gradeRetrieval(c,'assignment-target',{returnedRows:[{guid:'own',autoApproved:false}],sourceRequests:[]});
 assert.equal(result.ambiguousApprovedExposure,null);
 assert.equal(result.ambiguousAutoApprovalRisk,0);
 assert.equal(result.measurementStatus,'INCOMPLETE');
});
