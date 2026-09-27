import { createHash } from 'node:crypto';

const definitions = [
  ['participant-fixtures', [['NFL','American Football'],['NBA','Basketball'],['NHL','Ice Hockey'],['MLB','Baseball']]],
  ['fight-cards', [['UFC','Fighting'],['ONE Championship','Fighting'],['PFL','Fighting'],['Glory','Kickboxing']]],
  ['motorsport-sessions', [['Formula 1','Motorsport'],['MotoGP','Motorsport'],['IndyCar','Motorsport'],['Formula E','Motorsport']]],
  ['tournament-matches', [['ATP','Tennis'],['WTA','Tennis'],['BWF','Badminton'],['World Snooker Tour','Snooker']]],
  ['multi-day-stages', [['Tour de France','Cycling'],['Giro d Italia','Cycling'],['Vuelta a Espana','Cycling'],['Dakar Rally','Motorsport']]],
  ['discipline-meets', [['Diamond League','Athletics'],['World Aquatics','Swimming'],['FIG World Cup','Gymnastics'],['FIS World Cup','Skiing']]],
  ['recurring-broadcasts', [['WWE Raw','Wrestling'],['AEW Dynamite','Wrestling'],['ROH HonorClub','Wrestling'],['TNA Impact','Wrestling']]],
  ['unknown-partial', [['Regional Rugby','Rugby'],['Regional Cricket','Cricket'],['Regional Volleyball','Volleyball'],['Regional Handball','Handball']]],
];
export const shapes = Object.freeze(definitions.map(([shape]) => shape));
const variants = ['canonical','aliases-only','reversed-only','compact-date-only','broadcast-local-date-only','doubleheader','gender-age','localized-only','abbreviated-only','pack-only'];
const places = ['Aster Bay','Birch Harbour','Cedar Ridge','Delta Vale','Elm Point','Fir Coast','Granite Quay','Hazel Lake','Iris Field','Juniper Hill'];
const teams = ['Aster Falcons','Birch Comets','Cedar Owls','Delta Foxes','Elm Ravens','Fir Otters','Granite Wolves','Hazel Lynx','Iris Cranes','Juniper Bears'];
const disciplines = ['100m Final','200m Butterfly Final','Floor Final','Slalom Run 2'];
const sessions = ['Practice 1','Practice 2','Qualifying','Sprint','Race'];
const groups = ['ARENA','MOTION','FIELD','REPLAY'];
const languages = ['English','German','MULTi','French'];
const contentTypes = ['Highlights','Preview','Interview','First Half','Press Conference','Training','Radio Commentary','Condensed Replay'];
const digest = text => createHash('sha256').update(text).digest('hex');
const iso = (year, month, day, hour=18) => new Date(Date.UTC(year,month-1,day,hour)).toISOString();
const shift = (date,days) => new Date(Date.parse(date)+days*86400000).toISOString();
const dayToken = date => date.slice(0,10).replaceAll('-','.');
const eventDay = truth => truth.broadcastDate ?? truth.eventDate;
const shapeKinds = {
  'participant-fixtures':['opponent','home','gender','age','game'],
  'fight-cards':['opponent','home','card','part'],
  'motorsport-sessions':['session','round','location'],
  'tournament-matches':['opponent','round','location','gender','age'],
  'multi-day-stages':['stage','location','gender'],
  'discipline-meets':['discipline','location','gender','age'],
  'recurring-broadcasts':['episode'],
  'unknown-partial':['opponent','home','gender','age','game'],
};

function canonical(shape,t) {
  const date=dayToken(eventDay(t)), division=`${t.gender} ${t.ageGroup}`;
  if(shape==='participant-fixtures'||shape==='unknown-partial') return `${t.league} ${date} ${t.home} vs ${t.away} Game ${t.game} ${division}`;
  if(shape==='fight-cards') return `${t.league} ${t.card} ${date} ${t.home} vs ${t.away} ${t.part}`;
  if(shape==='motorsport-sessions') return `${t.league} ${t.season} Round${String(t.round).padStart(2,'0')} ${t.location} ${t.session} ${date}`;
  if(shape==='tournament-matches') return `${t.league} ${t.location} Open ${date} ${t.roundName} ${t.home} vs ${t.away} ${division}`;
  if(shape==='multi-day-stages') return `${t.league} ${t.season} Stage ${t.stage} ${t.location} ${date} ${division}`;
  if(shape==='discipline-meets') return `${t.league} ${date} ${t.location} ${t.discipline} ${division}`;
  return `${t.league} ${date} Episode ${t.episode}`;
}
function metadataTitle(shape,t) {
  if(shape==='participant-fixtures'||shape==='unknown-partial') return `${t.home} vs ${t.away} Game ${t.game} ${t.gender} ${t.ageGroup}`;
  if(shape==='fight-cards') return `${t.league} ${t.card}: ${t.home} vs ${t.away}`;
  if(shape==='motorsport-sessions') return `${t.location} Grand Prix ${t.session}`;
  if(shape==='multi-day-stages') return `${t.league} Stage ${t.stage} ${t.location}`;
  return canonical(shape,t);
}
export function semanticEventId(truth) {
  const fields=Object.fromEntries(Object.entries(truth).filter(([key])=>!['eventId','title','part'].includes(key)).sort(([a],[b])=>a.localeCompare(b)).map(([key,value])=>[key,typeof value==='string'?value.normalize('NFKC').trim().toLowerCase():value]));
  return 'event-'+digest(JSON.stringify(fields)).slice(0,32);
}
function neighbouring(shape,truth,kind) {
  const t=structuredClone(truth);
  if(kind==='date'||kind==='future'||kind==='rematch') {
    const days=kind==='future'?7:kind==='rematch'?-14:-2;
    t.eventDate=shift(t.eventDate,days);if(t.broadcastDate)t.broadcastDate=shift(t.broadcastDate,days);
    t.card+=days;t.round+=days;t.stage+=days;t.episode+=days;
  } else if(kind==='season') {
    t.season=String(Number(t.season)-1);t.eventDate=t.eventDate.replace(/^\d{4}/,t.season);if(t.broadcastDate)t.broadcastDate=t.broadcastDate.replace(/^\d{4}/,t.season);
  } else if(kind==='opponent'){t.away='Kestrel Badgers';if(shape==='fight-cards')t.card+=20;}
  else if(kind==='home'){t.home='Laurel Stags';if(shape==='fight-cards')t.card+=40;}
  else if(kind==='gender')t.gender=t.gender==='Women'?'Men':'Women';
  else if(kind==='age')t.ageGroup=t.ageGroup==='Senior'?'U21':'Senior';
  else if(kind==='game')t.game=t.game===1?2:1;
  else if(kind==='card')t.card+=1;
  else if(kind==='part')t.part=t.part==='Main Card'?(t.league==='ONE Championship'?'Lead Card':'Prelims'):'Main Card';
  else if(kind==='session')t.session=t.session==='Race'?'Qualifying':'Race';
  else if(kind==='round'){t.round+=1;t.roundName='Quarter Final';}
  else if(kind==='location')t.location='Laurel Valley';
  else if(kind==='stage')t.stage+=2;
  else if(kind==='discipline')t.discipline='Mixed Relay Final';
  else if(kind==='episode')t.episode+=1;
  else if(kind==='league'){t.league='Continental Regional Series';t.leagueAlias='CRS';}
  else throw Error(`Unknown identity mutation ${kind}`);
  t.eventId=semanticEventId(t);
  t.title=metadataTitle(shape,t);
  return t;
}
function packMembers(shape,truth,includesTarget) {
  const start=includesTarget?structuredClone(truth):neighbouring(shape,truth,'future');
  return [start,neighbouring(shape,start,'future')];
}
function render(shape,truth,naming,coverage,groupIndex) {
  const t=structuredClone(truth);
  if(naming==='aliases-only'||naming==='abbreviated-only') {
    t.home=t.home.split(' ').at(-1);t.away=t.away.split(' ').at(-1);
    t.league=t.leagueAlias;
  }
  if(naming==='reversed-only')[t.home,t.away]=[t.away,t.home];
  let title=canonical(shape,t);
  if(naming==='compact-date-only')title=title.replaceAll(dayToken(eventDay(t)),eventDay(t).slice(0,10).replaceAll('-',''));
  if(naming==='localized-only')title=title.replace('Stage ','Etappe ').replace('Qualifying','Qualifikation').replace('Race','Rennen').replace(' vs ',' contre ');
  if(coverage.kind==='pack') {
    const last=coverage.members.at(-1);
    title=shape==='multi-day-stages'?`${t.league} ${t.season} Stages ${t.stage} and ${last.stage} ${t.gender} ${t.ageGroup} Pack`
      :shape==='recurring-broadcasts'?`${t.league} Episodes ${t.episode} and ${last.episode} ${eventDay(t).slice(0,10)} Pack`
      :`${title} and ${canonical(shape,last)} Event Pack`;
  } else if(coverage.kind!=='complete') title+=` ${coverage.kind}`;
  const format=groupIndex%2===0?'720p WEB-DL H264':'1080p WEB-DL H265';
  return `${title} ${format} ${languages[groupIndex]}-${groups[groupIndex]}`.replaceAll(' ','.');
}
function makeCase(shape,shapeIndex,leagueInfo,leagueIndex,index) {
  const [league,sport]=leagueInfo;
  const split=[(leagueIndex*2+shapeIndex)%10,(leagueIndex*2+shapeIndex+5)%10].includes(index)?'holdout':'development';
  const year=split==='holdout'?2024:2020;
  const id=`sv2-${String(shapeIndex+1).padStart(2,'0')}-${leagueIndex+1}-${String(index+1).padStart(2,'0')}`;
  const knownAvailable=!(leagueIndex%2===0?[2,6]:[1,5,9]).includes(index);
  const naming=variants[index];
  const truth={eventId:id,league,leagueAlias:league==='Formula 1'?'F1':league.replaceAll(' ',''),sport,season:String(year),
    eventDate:iso(year,index===8?12:Math.floor(index/2)+1,index===8?31:4+index,index===4?1:18),
    broadcastDate:index===4?iso(year,3,7):null,home:teams[index],away:teams[(index+3)%10],
    card:9000+leagueIndex*100+index,location:places[index],round:index+21,roundName:['Final','Semi Final','Round 1','Round 2','Round 3'][index%5],
    stage:index+21,discipline:disciplines[leagueIndex],session:sessions[index%5],
    part:shape==='fight-cards'?(leagueIndex===0?['Main Card','Prelims','Main Card','Early Prelims'][index%4]:leagueIndex===1?['Main Card','Lead Card'][index%2]:['Main Card','Prelims'][index%2]):null,
    game:index===5?2:1,gender:index%3===0?'Women':'Men',ageGroup:index===6?'U21':'Senior',episode:100+index};
  truth.eventId=semanticEventId(truth);
  truth.title=metadataTitle(shape,truth);
  const metadata={id,externalId:`synthetic:${id}`,title:truth.title,sport,league:{name:league,alternateNames:[truth.leagueAlias]},season:truth.season,
    eventDate:truth.eventDate,broadcastDate:truth.broadcastDate,homeTeamName:truth.home,awayTeamName:truth.away,
    homeTeam:{name:truth.home,aliases:[truth.home.split(' ').at(-1)]},awayTeam:{name:truth.away,aliases:[truth.away.split(' ').at(-1)]},round:String(truth.round),monitored:true};
  let metadataConfidence='complete';
  if(shape==='unknown-partial') {
    metadataConfidence=index%2===0?'partial':'incorrect';
    if(index%2===0){metadata.homeTeamName=null;metadata.awayTeamName=null;metadata.homeTeam=null;metadata.awayTeam=null;metadata.title=`${league} Match ${index+1}`;}
    else {metadata.eventDate=shift(truth.eventDate,-2);metadata.broadcastDate=null;metadata.title=`${truth.home} vs ${truth.away}`;}
  }
  const ambiguous=metadataConfidence!=='complete';
  const kinds=[...shapeKinds[shape],'date','season','rematch','league','future','pack',...contentTypes];
  const observations=Array.from({length:20},(_,slot)=>{
    const relevant=knownAvailable&&slot<4;
    const kind=relevant?null:kinds[(slot-(knownAvailable?4:0))%kinds.length];
    let content=structuredClone(truth),coverage;
    if(relevant)coverage=naming==='pack-only'?{kind:'pack',members:packMembers(shape,truth,true)}:{kind:'complete',members:[content]};
    else if(kind==='pack'){const members=packMembers(shape,truth,false);content=members[0];coverage={kind:'pack',members};}
    else if(contentTypes.includes(kind))coverage={kind,members:[content]};
    else {content=neighbouring(shape,truth,kind);coverage={kind:'complete',members:[content]};}
    const style=slot%4;
    const guid=`release-${digest(`${id}/${slot}/neutral-v2`).slice(0,32)}`;
    const allowedAssignments=relevant?[{eventId:id,part:truth.part}]:[];
    return {guid,synthetic:true,split,title:render(shape,content,naming,coverage,style),publishDate:shift(truth.eventDate,index===7?35:style+1),
      category:5060,size:[1800000000,2200000000,1950000000,2100000000][style],seeders:10,protocol:style%2===0?'Torrent':'Usenet',
      releaseGroup:groups[style],language:languages[style],
      payload:{key:relevant?`${id}/complete`:`${guid}/content`,materialized:false,sha256:null},contentIdentity:content,coverage,
      label:{relevant,decision:relevant?(ambiguous?'relevant-ambiguous':'exact-assignment'):'irrelevant',reason:relevant?'covers-target':kind,allowedAssignments}};
  }).sort((a,b)=>a.guid.localeCompare(b.guid));
  const relevantReleaseGuids=observations.filter(o=>o.label.relevant).map(o=>o.guid);
  const coverage=[naming,...shapeKinds[shape],...(index===4?['broadcast-local-date']:[]),...(index===8?['season-boundary']:[]),...(index===7?['late-post']:[]),
    ...(ambiguous?[metadataConfidence==='partial'?'missing-participant-identity':'stale-date']:[])];
  return {id,synthetic:true,shape,leagueProfile:league,namingVariant:naming,split,knownAvailable,metadataConfidence,metadata,truth,requestedPart:truth.part,
    relevantReleaseGuids,observations,coverage,profile:{name:'Synthetic all-language WEB 720p and 1080p',allowedQualities:['WEBDL-720p','WEBDL-1080p'],languages:'any',allowHighlights:false,minSeeders:1,monitored:true},
    source:{inventoryId:`inventory-${split}`,model:['and','phrase','ordered'][index%3],category:5060,pageSize:[5,10,100][index%3],quota:20},
    expectedPolicies:{interactive:{showRelevant:relevantReleaseGuids,assignment:ambiguous?'requires-confirmation':'verified'},
      confirmedManual:{decision:knownAvailable?'grab':'no-grab'},userAutomatic:{decision:ambiguous?'abstain':knownAvailable?'grab':'no-grab'},
      unattended:{decision:ambiguous?'abstain':knownAvailable?'grab':'no-grab'},rss:{decision:ambiguous?'abstain':knownAvailable?'grab':'no-grab'}},
    expectedFinalFiles:knownAvailable&&!ambiguous?[{eventId:id,part:truth.part,payloadKey:`${id}/complete`,sha256:null}]:[],
    expectedConfirmedFiles:knownAvailable?[{eventId:id,part:truth.part,payloadKey:`${id}/complete`,sha256:null}]:[]};
}
export function publicObservation(observation) {
  const {guid,title,publishDate,category,size,seeders,protocol}=observation;
  return {guid,title,publishDate,category,size,seeders,protocol};
}
export function createCorpus() {
  const cases=definitions.flatMap(([shape,leagues],shapeIndex)=>leagues.flatMap((league,leagueIndex)=>Array.from({length:10},(_,index)=>makeCase(shape,shapeIndex,league,leagueIndex,index))));
  const inventories=['development','holdout'].map(split=>({id:`inventory-${split}`,split,releaseGuids:cases.filter(c=>c.split===split).flatMap(c=>c.observations.map(o=>o.guid)).sort((a,b)=>digest(a).localeCompare(digest(b)))}));
  return {schemaVersion:2,version:'synthetic-search-v4',seed:'fixed-enumeration-v2',evaluationTime:'2026-09-06T00:00:00.000Z',provenance:'Entirely synthetic. League names are naming profiles, not verified inventory.',
    holdoutRule:'Two rotated naming slots per profile use season2024. Development uses2020. Prior-season decoys use2023 and2019.',payloadScope:'Logical retrieval and identity only. No materialized media.',cases,inventories};
}
export function corpusHash(corpus=createCorpus()){return digest(JSON.stringify(corpus));}
