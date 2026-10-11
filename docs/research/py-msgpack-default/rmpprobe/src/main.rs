fn main() {
    let json_obj = br#"{"enabled":true,"kind":"transform","schema":"brokkr.sync.var.v0"}"#;
    let r: Result<serde_json::Value, _> = rmp_serde::from_slice(json_obj);
    println!("rmp_serde on a JSON object payload: {:?}", r);
    let mp = rmp_serde::to_vec(&serde_json::json!({"schema":"x","n":1})).unwrap();
    println!("rmp_serde::to_vec of a json! object, first byte {:#04x} len {}", mp[0], mp.len());
    let j: Result<serde_json::Map<String, serde_json::Value>, _> = serde_json::from_slice(&mp);
    println!("serde_json on that MessagePack map: {:?}", j.map(|_| ()).map_err(|e| e.to_string()));
    let mut de = rmp_serde::Deserializer::from_read_ref(&json_obj[..]);
    let v: serde_json::Value = serde::Deserialize::deserialize(&mut de).unwrap();
    println!("one_value(json obj) = {:?}; one_value(mp) = {:?}", one_value(json_obj), one_value(&mp).map(|_| ()));
    println!("consumed {} of {} bytes, value {:?}", de.get_ref().len() as usize, json_obj.len(), v);
}
#[allow(dead_code)]
fn one_value(bytes: &[u8]) -> Result<serde_json::Value, String> {
    let mut de = rmp_serde::Deserializer::new(std::io::Cursor::new(bytes));
    let v: serde_json::Value = serde::Deserialize::deserialize(&mut de).map_err(|e| e.to_string())?;
    let used = de.get_ref().position() as usize;
    if used != bytes.len() { return Err(format!("{} trailing bytes after the first MessagePack value", bytes.len() - used)); }
    Ok(v)
}
