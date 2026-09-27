import test from 'node:test';
import assert from 'node:assert/strict';
import { createCorpus, corpusHash, shapes, publicObservation, semanticEventId } from './corpus.mjs';
const corpus=createCorpus();

test('320 cases retain eight shapes, four profiles and 30/10 availability splits',()=>{
  assert.equal(corpus.cases.length,320);assert.equal(shapes.length,8);
  for(const shape of shapes){const cases=corpus.cases.filter(c=>c.shape===shape);assert.equal(cases.length,40);assert.equal(cases.filter(c=>c.knownAvailable).length,30);assert.equal(new Set(cases.map(c=>c.leagueProfile)).size,4);assert.equal(cases.filter(c=>c.split==='holdout').length,8);}
});

test('content coverage independently determines relevance and event identities',()=>{
  const guids=new Set();
  for(const c of corpus.cases){
    assert.equal(c.observations.length,20);assert.equal(c.relevantReleaseGuids.length,c.knownAvailable?4:0);
    assert.equal(new Set(c.observations.map(o=>o.title)).size,20);
    for(const o of c.observations){
      assert.ok(!guids.has(o.guid));guids.add(o.guid);
      const covers=(o.coverage.kind==='complete'||o.coverage.kind==='pack')&&o.coverage.members.some(m=>m.eventId===c.truth.eventId&&m.part===c.requestedPart);
      assert.equal(covers,o.label.relevant,`${c.id} ${o.label.reason}`);
      assert.equal(c.relevantReleaseGuids.includes(o.guid),covers);
      for(const m of o.coverage.members){assert.equal(new Date(m.eventDate).toISOString(),m.eventDate);if(m.eventId===c.truth.eventId){const{part,...actual}=m;const{part:_,...expected}=c.truth;assert.deepEqual(actual,expected);}}
      if(!o.label.relevant&&o.coverage.kind==='complete')assert.ok(o.contentIdentity.eventId!==c.truth.eventId||o.contentIdentity.part!==c.requestedPart);
      if(o.label.reason==='pack')assert.ok(!o.coverage.members.some(m=>m.eventId===c.truth.eventId));
      assert.equal(o.payload.materialized,false);assert.equal(o.payload.sha256,null);
    }
  }
});

test('public release fields contain no oracle tags or exact fixture identity',()=>{
  const distribution=new Map();
  for(const c of corpus.cases)for(const o of c.observations){
    const pub=publicObservation(o);
    assert.deepEqual(Object.keys(pub),['guid','title','publishDate','category','size','seeders','protocol']);
    assert.ok(!/SYNTH|NEG|wrong[._ ]|excludes[._ ]target|sv2-|synthetic:|release-.*-other/i.test(pub.title));
    assert.match(pub.guid,/^release-[a-f0-9]{32}$/);
    for(const [field,value] of Object.entries({group:o.releaseGroup,language:o.language,size:o.size,protocol:o.protocol,seeders:o.seeders})){
      const key=field+'/'+value;const counts=distribution.get(key)??[0,0];counts[o.label.relevant?1:0]++;distribution.set(key,counts);
    }
  }
  for(const [key,[negative,positive]]of distribution){assert.ok(negative>0&&positive>0,key);assert.equal(negative/5440,positive/960,key);}
});

test('date negatives are observably distinct and source inventories mix event cases',()=>{
  for(const c of corpus.cases){
    for(const o of c.observations.filter(o=>o.label.reason==='date')){
      const actual=(o.contentIdentity.broadcastDate??o.contentIdentity.eventDate).slice(0,10);
      const target=(c.truth.broadcastDate??c.truth.eventDate).slice(0,10);
      assert.notEqual(actual,target);
    }
    const inventory=corpus.inventories.find(i=>i.id===c.source.inventoryId);
    assert.ok(inventory.releaseGuids.length>=1280);
    for(const o of c.observations)assert.ok(inventory.releaseGuids.includes(o.guid));
  }
  for(const i of corpus.inventories){assert.equal(new Set(i.releaseGuids).size,i.releaseGuids.length);assert.ok(i.releaseGuids.some((guid,index)=>index>0&&guid<i.releaseGuids[index-1]));}
});

test('ambiguous metadata requires abstention and actual participant names are missing',()=>{
  for(const c of corpus.cases.filter(c=>c.shape==='unknown-partial')){
    for(const route of ['userAutomatic','unattended','rss'])assert.equal(c.expectedPolicies[route].decision,'abstain');
    assert.equal(c.expectedFinalFiles.length,0);
    assert.equal(c.expectedConfirmedFiles.length,c.knownAvailable?1:0);
    if(c.metadataConfidence==='partial'){assert.equal(c.metadata.homeTeamName,null);assert.equal(c.metadata.awayTeamName,null);assert.equal(c.metadata.homeTeam,null);assert.equal(c.metadata.awayTeam,null);assert.ok(!c.metadata.title.includes(c.truth.home));}
  }
});

test('exclusive renderings and coverage tags reflect actual examples',()=>{
  const holdout=new Set(corpus.cases.filter(c=>c.split==='holdout').map(c=>c.namingVariant));
  assert.equal(holdout.size,10);
  for(const c of corpus.cases){
    if(c.shape!=='motorsport-sessions')assert.ok(!c.coverage.includes('session'));
    if(c.shape!=='multi-day-stages')assert.ok(!c.coverage.includes('stage'));
    for(const o of c.observations.filter(o=>o.label.relevant)){
      if(c.namingVariant==='aliases-only'||c.namingVariant==='abbreviated-only')assert.ok(!o.title.includes(c.truth.home.replaceAll(' ','.')));
      if(c.namingVariant==='compact-date-only')assert.ok(!o.title.includes((c.truth.broadcastDate??c.truth.eventDate).slice(0,10).replaceAll('-','.')));
      if(c.namingVariant==='pack-only')assert.equal(o.coverage.kind,'pack');
    }
  }
});

test('holdout has disjoint content seasons and all equivalent payloads stay together',()=>{
  const seasons={development:new Set(),holdout:new Set()},payloads=new Map();
  for(const c of corpus.cases){
    for(const o of c.observations){for(const m of o.coverage.members)seasons[c.split].add(m.season);if(payloads.has(o.payload.key))assert.equal(payloads.get(o.payload.key),c.split);payloads.set(o.payload.key,c.split);}
  }
  for(const season of seasons.holdout)assert.ok(!seasons.development.has(season));
  assert.deepEqual([...seasons.holdout].sort(),['2023','2024']);
});

test('generation is deterministic and mutation changes fingerprint',()=>{
  const fresh=createCorpus();assert.deepEqual(corpus,fresh);assert.equal(corpusHash(corpus),corpusHash(fresh));fresh.cases[0].metadata.title+=' changed';assert.notEqual(corpusHash(corpus),corpusHash(fresh));
});

test('semantic content identity is stable across standalone and pack observations',()=>{
  const identities=new Map();
  let repeated=0;
  for(const c of corpus.cases)for(const o of c.observations)for(const member of o.coverage.members){
    const {eventId,title,part,...fields}=member;
    const key=JSON.stringify(Object.fromEntries(Object.entries(fields).sort(([a],[b])=>a.localeCompare(b))));
    assert.equal(eventId,semanticEventId(member));
    if(identities.has(key)){assert.equal(eventId,identities.get(key));repeated++;}
    identities.set(key,eventId);
  }
  assert.ok(repeated>500);
});

test('one league card and date has one event identity across participant descriptions',()=>{
  const cards=new Map();
  for(const c of corpus.cases.filter(c=>c.shape==='fight-cards'))for(const o of c.observations)for(const member of o.coverage.members){
    const key=[member.league,member.card,member.eventDate].join('|');
    if(cards.has(key))assert.equal(member.eventId,cards.get(key),key);
    cards.set(key,member.eventId);
    if(['home','opponent'].includes(o.label.reason))assert.notEqual(member.card,c.truth.card);
  }
});

test('all metadata, content and publication dates precede the real B0 evaluation era',()=>{
  assert.equal(corpus.evaluationTime,'2026-09-06T00:00:00.000Z');
  const cutoff=Date.parse(corpus.evaluationTime);
  for(const c of corpus.cases){
    for(const field of ['eventDate','broadcastDate'])if(c.metadata[field])assert.ok(Date.parse(c.metadata[field])<cutoff,`${c.id} metadata ${field}`);
    for(const o of c.observations){
      assert.ok(Date.parse(o.publishDate)<cutoff,`${o.guid} publication`);
      for(const member of o.coverage.members)for(const field of ['eventDate','broadcastDate'])if(member[field])assert.ok(Date.parse(member[field])<cutoff,`${member.eventId} ${field}`);
    }
  }
});
