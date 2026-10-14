use proc_macro::TokenStream;
use quote::format_ident;
use quote::quote;
use syn::Data;
use syn::DeriveInput;
use syn::Fields;
use syn::LitInt;
use syn::LitStr;
use syn::Type;
use syn::parse_macro_input;

#[proc_macro_derive(DatabaseEntry, attributes(cultcache))]
pub fn derive_database_entry(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    match database_entry_codec(input) {
        Ok(tokens) => tokens.into(),
        Err(error) => error.to_compile_error().into(),
    }
}

fn database_entry_codec(input: DeriveInput) -> syn::Result<proc_macro2::TokenStream> {
    let ident = input.ident.clone();
    let mut entry_type: Option<LitStr> = None;
    let mut schema_name: Option<LitStr> = None;

    for attribute in input
        .attrs
        .iter()
        .filter(|attr| attr.path().is_ident("cultcache"))
    {
        let parse_result = attribute.parse_nested_meta(|meta| {
            if meta.path.is_ident("type") {
                once(&entry_type, &meta, "type")?;
                entry_type = Some(meta.value()?.parse()?);
                Ok(())
            } else if meta.path.is_ident("schema") {
                once(&schema_name, &meta, "schema")?;
                schema_name = Some(meta.value()?.parse()?);
                Ok(())
            } else {
                Err(meta.error("unsupported cultcache attribute"))
            }
        });
        parse_result?;
    }

    let Some(entry_type) = entry_type else {
        return Err(syn::Error::new_spanned(
            ident,
            "DatabaseEntry derive requires #[cultcache(type = \"...\")]",
        ));
    };
    let schema_name = schema_name.unwrap_or_else(|| LitStr::new(&ident.to_string(), ident.span()));

    let slots = named_slots(input.data, &ident, "DatabaseEntry")?;
    let codec = slot_codec(&ident, &slots);

    Ok(quote! {
        impl ::cultcache_rs::DatabaseEntry for #ident {
            const TYPE: &'static str = #entry_type;
            const SCHEMA_NAME: &'static str = #schema_name;
        }

        #codec
    })
}

/// A named struct whose fields carry `#[cultcache(key = N)]`: the same positional
/// array a `DatabaseEntry` is, without the entry consts. The payload of a union arm,
/// or any nested record that crosses runtimes.
#[proc_macro_derive(CultSlots, attributes(cultcache))]
pub fn derive_cult_slots(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    match cult_slots_codec(input) {
        Ok(tokens) => tokens.into(),
        Err(error) => error.to_compile_error().into(),
    }
}

fn cult_slots_codec(input: DeriveInput) -> syn::Result<proc_macro2::TokenStream> {
    let ident = input.ident.clone();
    if !input.generics.params.is_empty() {
        return Err(syn::Error::new_spanned(ident, "CultSlots derive does not support generics"));
    }
    let slots = named_slots(input.data, &ident, "CultSlots")?;
    Ok(slot_codec(&ident, &slots))
}

/// An enum written as `[key, armSlots]`. Every variant carries `#[cultcache(key = N)]`
/// and is a unit (`[N, []]`) or a newtype over a type that serializes as an array.
/// A key the enum does not declare is a decode error; there is no catch-all arm.
#[proc_macro_derive(CultUnion, attributes(cultcache))]
pub fn derive_cult_union(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    match union_codec(input) {
        Ok(tokens) => tokens.into(),
        Err(error) => error.to_compile_error().into(),
    }
}

/// The largest array16 length: a slot record is written as a dense array, so a key above it would
/// make every encode allocate and write an unbounded run of nils. Every runtime and the C# catalog
/// refuse the same domain.
const MAX_SLOT_KEY: usize = 65535;

fn named_slots(data: Data, ident: &syn::Ident, owner: &str) -> syn::Result<Vec<CultCacheSlot>> {
    let fields = match data {
        Data::Struct(data) => match data.fields {
            Fields::Named(fields) => fields.named,
            _ => {
                return Err(syn::Error::new_spanned(
                    ident,
                    format!("{owner} derive requires a named struct"),
                ));
            }
        },
        _ => {
            return Err(syn::Error::new_spanned(
                ident,
                format!("{owner} derive requires a struct"),
            ));
        }
    };

    let mut slots: Vec<CultCacheSlot> = Vec::new();
    for field in fields {
        let Some(field_ident) = field.ident else {
            return Err(syn::Error::new_spanned(
                field.ty,
                format!("{owner} derive requires named fields"),
            ));
        };
        let mut key: Option<usize> = None;
        let mut default = false;
        let mut bytes = false;
        for attribute in field
            .attrs
            .iter()
            .filter(|attr| attr.path().is_ident("cultcache"))
        {
            attribute.parse_nested_meta(|meta| {
                if meta.path.is_ident("key") {
                    once(&key, &meta, "key")?;
                    let value: LitInt = meta.value()?.parse()?;
                    let slot: usize = value.base10_parse()?;
                    if slot > MAX_SLOT_KEY {
                        return Err(syn::Error::new_spanned(
                            &value,
                            format!("{owner} slot key {slot} is outside 0..={MAX_SLOT_KEY}"),
                        ));
                    }
                    key = Some(slot);
                    Ok(())
                } else if meta.path.is_ident("default") {
                    default = true;
                    Ok(())
                } else if meta.path.is_ident("bytes") {
                    bytes = true;
                    Ok(())
                } else {
                    Err(meta.error("unsupported field cultcache attribute"))
                }
            })?;
        }
        let Some(key) = key else {
            return Err(syn::Error::new_spanned(
                field_ident,
                format!("{owner} fields require #[cultcache(key = N)]"),
            ));
        };
        if slots.iter().any(|slot| slot.key == key) {
            return Err(syn::Error::new_spanned(
                field_ident,
                format!("duplicate {owner} slot {key}"),
            ));
        }
        slots.push(CultCacheSlot {
            key,
            ident: field_ident,
            ty: field.ty,
            default,
            bytes,
        });
    }
    slots.sort_by_key(|slot| slot.key);
    Ok(slots)
}

/// The one slot generator: Serialize as a positional array with nil gaps, Deserialize
/// by slot with the `default` and `bytes` attributes.
fn slot_codec(ident: &syn::Ident, slots: &[CultCacheSlot]) -> proc_macro2::TokenStream {
    let max_slot = slots.iter().map(|slot| slot.key).max().unwrap_or(0);

    let serialize_elements = (0..=max_slot).map(|slot| {
        if let Some(field) = slots.iter().find(|field| field.key == slot) {
            let field_ident = &field.ident;
            if field.bytes {
                quote! {
                    tuple.serialize_element(::serde_bytes::Bytes::new(&self.#field_ident))?;
                }
            } else {
                quote! {
                    tuple.serialize_element(&self.#field_ident)?;
                }
            }
        } else {
            quote! {
                tuple.serialize_element(&())?;
            }
        }
    });

    let option_declarations = slots.iter().map(|field| {
        let variable = format_ident!("slot_{}", field.key);
        let ty = &field.ty;
        if field.bytes {
            quote! {
                let mut #variable: ::std::option::Option<::serde_bytes::ByteBuf> = ::std::option::Option::None;
            }
        } else {
            quote! {
                let mut #variable: ::std::option::Option<#ty> = ::std::option::Option::None;
            }
        }
    });

    let deserialize_match_arms = (0..=max_slot).map(|slot| {
        if let Some(field) = slots.iter().find(|field| field.key == slot) {
            let variable = format_ident!("slot_{}", field.key);
            let ty = &field.ty;
            if field.bytes {
                quote! {
                    #slot => {
                        #variable = seq.next_element::<::serde_bytes::ByteBuf>()?;
                    }
                }
            } else {
                quote! {
                    #slot => {
                        #variable = seq.next_element::<#ty>()?;
                    }
                }
            }
        } else {
            quote! {
                #slot => {
                    let _ = seq.next_element::<::serde::de::IgnoredAny>()?;
                }
            }
        }
    });

    let build_fields = slots.iter().map(|field| {
        let field_ident = &field.ident;
        let variable = format_ident!("slot_{}", field.key);
        if field.bytes && field.default {
            quote! {
                #field_ident: #variable.unwrap_or_default().into_vec()
            }
        } else if field.bytes {
            let field_name = field_ident.to_string();
            quote! {
                #field_ident: #variable.ok_or_else(|| ::serde::de::Error::missing_field(#field_name))?.into_vec()
            }
        } else if field.default {
            quote! {
                #field_ident: #variable.unwrap_or_default()
            }
        } else {
            let field_name = field_ident.to_string();
            quote! {
                #field_ident: #variable.ok_or_else(|| ::serde::de::Error::missing_field(#field_name))?
            }
        }
    });

    quote! {
        const _: () = ::core::assert!(
            ::core::mem::size_of::<#ident>() <= ::cultcache_rs::MAX_RECORD_INLINE_BYTES,
            "a CultCache record is at most cultcache_rs::MAX_RECORD_INLINE_BYTES bytes inline; box its largest slot"
        );

        impl ::cultcache_rs::SlotRecord for #ident {
            fn __slots_array() {}
        }

        impl ::serde::Serialize for #ident {
            fn serialize<__S>(&self, serializer: __S) -> ::std::result::Result<__S::Ok, __S::Error>
            where
                __S: ::serde::Serializer,
            {
                use ::serde::ser::SerializeTuple;
                let mut tuple = serializer.serialize_tuple(#max_slot + 1)?;
                #(#serialize_elements)*
                tuple.end()
            }
        }

        impl<'de> ::serde::Deserialize<'de> for #ident {
            fn deserialize<__D>(deserializer: __D) -> ::std::result::Result<Self, __D::Error>
            where
                __D: ::serde::Deserializer<'de>,
            {
                struct __CultCacheVisitor;

                impl<'de> ::serde::de::Visitor<'de> for __CultCacheVisitor {
                    type Value = #ident;

                    fn expecting(&self, formatter: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
                        formatter.write_str(concat!("a CultCache array for ", stringify!(#ident)))
                    }

                    fn visit_seq<__A>(self, mut seq: __A) -> ::std::result::Result<Self::Value, __A::Error>
                    where
                        __A: ::serde::de::SeqAccess<'de>,
                    {
                        #(#option_declarations)*
                        let mut index = 0usize;
                        while index <= #max_slot {
                            match index {
                                #(#deserialize_match_arms)*
                                _ => unreachable!(),
                            }
                            index += 1;
                        }
                        while let ::std::option::Option::Some(_) = seq.next_element::<::serde::de::IgnoredAny>()? {}
                        Ok(#ident {
                            #(#build_fields),*
                        })
                    }
                }

                deserializer.deserialize_tuple(#max_slot + 1, __CultCacheVisitor)
            }
        }
    }
}

/// An attribute names each thing once; a second `key = N` would silently win otherwise.
fn once<T>(seen: &Option<T>, meta: &syn::meta::ParseNestedMeta, name: &str) -> syn::Result<()> {
    if seen.is_some() {
        return Err(meta.error(format!("repeated `{name}`: #[cultcache({name} = ...)] is given once")));
    }
    Ok(())
}

/// An arm key is the reference's Int32, negatives included: `key = 5` or `key = -1`.
fn arm_key(meta: &syn::meta::ParseNestedMeta) -> syn::Result<i32> {
    let expr: syn::Expr = meta.value()?.parse()?;
    let (negative, literal) = match expr {
        syn::Expr::Lit(syn::ExprLit { lit: syn::Lit::Int(literal), .. }) => (false, literal),
        syn::Expr::Unary(syn::ExprUnary { op: syn::UnOp::Neg(_), expr, .. }) => match *expr {
            syn::Expr::Lit(syn::ExprLit { lit: syn::Lit::Int(literal), .. }) => (true, literal),
            other => return Err(syn::Error::new_spanned(other, "a CultUnion arm key is an integer literal")),
        },
        other => return Err(syn::Error::new_spanned(other, "a CultUnion arm key is an integer literal")),
    };
    let magnitude: i128 = literal.base10_parse()?;
    let value = if negative { -magnitude } else { magnitude };
    i32::try_from(value)
        .map_err(|_| syn::Error::new_spanned(&literal, format!("CultUnion arm key {value} is outside Int32")))
}

struct CultCacheSlot {
    key: usize,
    ident: syn::Ident,
    ty: Type,
    default: bool,
    bytes: bool,
}

fn union_codec(input: DeriveInput) -> syn::Result<proc_macro2::TokenStream> {
    let ident = input.ident.clone();
    if !input.generics.params.is_empty() {
        return Err(syn::Error::new_spanned(
            ident,
            "CultUnion derive does not support generics",
        ));
    }
    let Data::Enum(data) = input.data else {
        return Err(syn::Error::new_spanned(ident, "CultUnion derive requires an enum"));
    };
    if data.variants.is_empty() {
        return Err(syn::Error::new_spanned(ident, "CultUnion derive requires at least one variant"));
    }

    struct Arm {
        key: i32,
        ident: syn::Ident,
        payload: Option<Type>,
    }
    let mut arms: Vec<Arm> = Vec::new();
    for variant in data.variants {
        let mut key: Option<i32> = None;
        for attribute in variant
            .attrs
            .iter()
            .filter(|attr| attr.path().is_ident("cultcache"))
        {
            attribute.parse_nested_meta(|meta| {
                if meta.path.is_ident("key") {
                    once(&key, &meta, "key")?;
                    key = Some(arm_key(&meta)?);
                    Ok(())
                } else {
                    Err(meta.error("unsupported variant cultcache attribute"))
                }
            })?;
        }
        let Some(key) = key else {
            return Err(syn::Error::new_spanned(
                &variant.ident,
                "CultUnion variants require #[cultcache(key = N)]",
            ));
        };
        if arms.iter().any(|arm| arm.key == key) {
            return Err(syn::Error::new_spanned(
                &variant.ident,
                format!("duplicate CultUnion key {key}"),
            ));
        }
        let payload = match variant.fields {
            Fields::Unit => None,
            Fields::Unnamed(fields) if fields.unnamed.len() == 1 => {
                Some(fields.unnamed.into_iter().next().expect("one field").ty)
            }
            _ => {
                return Err(syn::Error::new_spanned(
                    &variant.ident,
                    "a CultUnion variant is a unit or a newtype: use a newtype over a #[derive(CultSlots)] struct",
                ));
            }
        };
        arms.push(Arm {
            key,
            ident: variant.ident,
            payload,
        });
    }

    let enum_name = ident.to_string();
    let mut declared_keys: Vec<i32> = arms.iter().map(|arm| arm.key).collect();
    declared_keys.sort_unstable();
    let declared = declared_keys.iter().map(i32::to_string).collect::<Vec<_>>().join(", ");
    let unknown_message = format!("unknown arm key {{}} for union {enum_name}; declared keys: {declared}");
    let arity_message = format!("union {enum_name} is not a [key, slots] pair");
    let non_integer_message = format!("union {enum_name} has a non-integer arm key; declared keys: {declared}");
    let nil_slots_message = format!("union {enum_name} arm {{}} has nil slots; declared keys: {declared}");
    let out_of_range_message = format!("union {enum_name} has an arm key outside Int32; declared keys: {declared}");
    let payload_checks = arms.iter().filter_map(|arm| arm.payload.as_ref()).map(|ty| {
        quote! { assert_slot_record::<#ty>(); }
    });

    let serialize_arms = arms.iter().map(|arm| {
        let variant = &arm.ident;
        let key = arm.key;
        if arm.payload.is_some() {
            quote! {
                Self::#variant(arm) => {
                    tuple.serialize_element(&#key)?;
                    tuple.serialize_element(arm)?;
                }
            }
        } else {
            quote! {
                Self::#variant => {
                    tuple.serialize_element(&#key)?;
                    tuple.serialize_element(&[(); 0])?;
                }
            }
        }
    });

    let deserialize_arms = arms.iter().map(|arm| {
        let variant = &arm.ident;
        let key = arm.key;
        if let Some(ty) = &arm.payload {
            quote! {
                #key => {
                    // A nil in the slots position is not an empty record.
                    match seq.next_element::<::std::option::Option<#ty>>()? {
                        ::std::option::Option::Some(::std::option::Option::Some(arm)) => #ident::#variant(arm),
                        ::std::option::Option::Some(::std::option::Option::None) => {
                            return ::std::result::Result::Err(::serde::de::Error::custom(
                                format!(#nil_slots_message, #key),
                            ));
                        }
                        ::std::option::Option::None => {
                            return ::std::result::Result::Err(::serde::de::Error::custom(#arity_message));
                        }
                    }
                }
            }
        } else {
            quote! {
                #key => {
                    match seq
                        .next_element::<::std::option::Option<[(); 0]>>()
                        .map_err(|_| ::serde::de::Error::custom(#arity_message))?
                    {
                        ::std::option::Option::Some(::std::option::Option::Some(_)) => #ident::#variant,
                        ::std::option::Option::Some(::std::option::Option::None) => {
                            return ::std::result::Result::Err(::serde::de::Error::custom(
                                format!(#nil_slots_message, #key),
                            ));
                        }
                        ::std::option::Option::None => {
                            return ::std::result::Result::Err(::serde::de::Error::custom(#arity_message));
                        }
                    }
                }
            }
        }
    });

    Ok(quote! {
        const _: () = ::core::assert!(
            ::core::mem::size_of::<#ident>() <= ::cultcache_rs::MAX_RECORD_INLINE_BYTES,
            "a CultCache record is at most cultcache_rs::MAX_RECORD_INLINE_BYTES bytes inline; box its largest slot"
        );

        // An arm is a slots array: a newtype over anything else is refused here, by the
        // trait the CultSlots and DatabaseEntry derives implement.
        const _: fn() = || {
            #[allow(dead_code)]
            fn assert_slot_record<T: ::cultcache_rs::SlotRecord>() {}
            #(#payload_checks)*
        };

        impl ::serde::Serialize for #ident {
            fn serialize<__S>(&self, serializer: __S) -> ::std::result::Result<__S::Ok, __S::Error>
            where
                __S: ::serde::Serializer,
            {
                use ::serde::ser::SerializeTuple;
                let mut tuple = serializer.serialize_tuple(2)?;
                match self {
                    #(#serialize_arms)*
                }
                tuple.end()
            }
        }

        impl<'de> ::serde::Deserialize<'de> for #ident {
            fn deserialize<__D>(deserializer: __D) -> ::std::result::Result<Self, __D::Error>
            where
                __D: ::serde::Deserializer<'de>,
            {
                // The reference classifies the key before the arm: an integer in Int32, an
                // integer outside it, or not an integer. No input value is kept or echoed.
                enum __ArmKey {
                    Int(i64),
                    OutOfRange,
                    NonInteger,
                }
                struct __ArmKeyProbe(__ArmKey);
                struct __ArmKeyVisitor;

                impl<'de> ::serde::de::Visitor<'de> for __ArmKeyVisitor {
                    type Value = __ArmKeyProbe;

                    fn expecting(&self, formatter: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
                        formatter.write_str("an integer arm key")
                    }

                    fn visit_i64<__E>(self, value: i64) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::Int(value)))
                    }

                    fn visit_u64<__E>(self, value: u64) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(match i64::try_from(value) {
                            Ok(value) => __ArmKey::Int(value),
                            Err(_) => __ArmKey::OutOfRange,
                        }))
                    }

                    fn visit_bool<__E>(self, _: bool) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_f64<__E>(self, _: f64) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_str<__E>(self, _: &str) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_bytes<__E>(self, _: &[u8]) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_unit<__E>(self) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_none<__E>(self) -> ::std::result::Result<Self::Value, __E> {
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_some<__D>(self, deserializer: __D) -> ::std::result::Result<Self::Value, __D::Error>
                    where
                        __D: ::serde::Deserializer<'de>,
                    {
                        <::serde::de::IgnoredAny as ::serde::Deserialize>::deserialize(deserializer)?;
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    // A MessagePack ext value (fixext, timestamp, ext8) reaches the visitor here.
                    fn visit_newtype_struct<__D>(self, deserializer: __D) -> ::std::result::Result<Self::Value, __D::Error>
                    where
                        __D: ::serde::Deserializer<'de>,
                    {
                        <::serde::de::IgnoredAny as ::serde::Deserialize>::deserialize(deserializer)?;
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_seq<__A>(self, mut seq: __A) -> ::std::result::Result<Self::Value, __A::Error>
                    where
                        __A: ::serde::de::SeqAccess<'de>,
                    {
                        while seq.next_element::<::serde::de::IgnoredAny>()?.is_some() {}
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }

                    fn visit_map<__A>(self, mut map: __A) -> ::std::result::Result<Self::Value, __A::Error>
                    where
                        __A: ::serde::de::MapAccess<'de>,
                    {
                        while map.next_entry::<::serde::de::IgnoredAny, ::serde::de::IgnoredAny>()?.is_some() {}
                        Ok(__ArmKeyProbe(__ArmKey::NonInteger))
                    }
                }

                impl<'de> ::serde::Deserialize<'de> for __ArmKeyProbe {
                    fn deserialize<__D>(deserializer: __D) -> ::std::result::Result<Self, __D::Error>
                    where
                        __D: ::serde::Deserializer<'de>,
                    {
                        deserializer.deserialize_any(__ArmKeyVisitor)
                    }
                }

                struct __CultUnionVisitor;

                impl<'de> ::serde::de::Visitor<'de> for __CultUnionVisitor {
                    type Value = #ident;

                    fn expecting(&self, formatter: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
                        formatter.write_str(concat!("a [key, slots] pair for union ", stringify!(#ident)))
                    }

                    fn visit_seq<__A>(self, mut seq: __A) -> ::std::result::Result<Self::Value, __A::Error>
                    where
                        __A: ::serde::de::SeqAccess<'de>,
                    {
                        if seq.size_hint().is_some_and(|len| len != 2) {
                            return ::std::result::Result::Err(::serde::de::Error::custom(#arity_message));
                        }
                        let key = match seq
                            .next_element::<__ArmKeyProbe>()
                            .map_err(|_| ::serde::de::Error::custom(#arity_message))?
                            .ok_or_else(|| ::serde::de::Error::custom(#arity_message))?
                            .0
                        {
                            __ArmKey::Int(wide) => match i32::try_from(wide) {
                                Ok(key) => key,
                                Err(_) => {
                                    return ::std::result::Result::Err(::serde::de::Error::custom(#out_of_range_message));
                                }
                            },
                            __ArmKey::OutOfRange => {
                                return ::std::result::Result::Err(::serde::de::Error::custom(#out_of_range_message));
                            }
                            __ArmKey::NonInteger => {
                                return ::std::result::Result::Err(::serde::de::Error::custom(#non_integer_message));
                            }
                        };
                        let value = match key {
                            #(#deserialize_arms)*
                            other => {
                                return ::std::result::Result::Err(::serde::de::Error::custom(
                                    format!(#unknown_message, other),
                                ));
                            }
                        };
                        if seq.next_element::<::serde::de::IgnoredAny>()?.is_some() {
                            return ::std::result::Result::Err(::serde::de::Error::custom(#arity_message));
                        }
                        Ok(value)
                    }
                }

                deserializer.deserialize_tuple(2, __CultUnionVisitor)
            }
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn union_error(source: &str) -> String {
        let input: DeriveInput = syn::parse_str(source).expect("the fixture parses");
        union_codec(input).expect_err("the fixture is refused").to_string()
    }

    fn slots_error(source: &str) -> String {
        let input: DeriveInput = syn::parse_str(source).expect("the fixture parses");
        let Err(error) = named_slots(input.data, &input.ident, "CultSlots") else {
            panic!("the fixture is refused");
        };
        error.to_string()
    }

    fn union_ok(source: &str) {
        let input: DeriveInput = syn::parse_str(source).expect("the fixture parses");
        union_codec(input).expect("the fixture is accepted");
    }

    // Keys other than 0 throughout, so a default of 0 cannot stand in for a missing key.
    #[test]
    fn a_variant_without_a_key_is_refused_for_that_reason() {
        let message = union_error("enum E { #[cultcache(key = 4)] A(Arm), B }");
        assert!(message.contains("require #[cultcache(key = N)]"), "{message}");
        let message = union_error("enum E { B, #[cultcache(key = 4)] A(Arm) }");
        assert!(message.contains("require #[cultcache(key = N)]"), "{message}");
    }

    #[test]
    fn two_variants_with_one_key_are_refused_for_that_reason() {
        let message = union_error("enum E { #[cultcache(key = 4)] A(Arm), #[cultcache(key = 4)] B }");
        assert!(message.contains("duplicate CultUnion key 4"), "{message}");
        let message = union_error("enum E { #[cultcache(key = 0x4)] A(Arm), #[cultcache(key = 4)] B }");
        assert!(message.contains("duplicate CultUnion key 4"), "{message}");
    }

    #[test]
    fn a_repeated_key_attribute_is_refused_on_a_variant_and_on_a_field() {
        for source in [
            "enum E { #[cultcache(key = 7)] #[cultcache(key = 0)] A(Arm) }",
            "enum E { #[cultcache(key = 7, key = 0)] A(Arm) }",
        ] {
            let message = union_error(source);
            assert!(message.contains("repeated `key`"), "{source}: {message}");
        }
        for source in [
            "struct S { #[cultcache(key = 7)] #[cultcache(key = 0)] a: String }",
            "struct S { #[cultcache(key = 7, key = 0)] a: String }",
        ] {
            let message = slots_error(source);
            assert!(message.contains("repeated `key`"), "{source}: {message}");
        }
    }

    #[test]
    fn a_duplicate_slot_is_refused_for_that_reason() {
        let message = slots_error("struct S { #[cultcache(key = 3)] a: String, #[cultcache(key = 3)] b: String }");
        assert!(message.contains("duplicate CultSlots slot 3"), "{message}");
    }

    #[test]
    fn a_field_without_a_key_is_refused_for_that_reason() {
        let message = slots_error("struct S { #[cultcache(key = 3)] a: String, b: String }");
        assert!(message.contains("fields require #[cultcache(key = N)]"), "{message}");
    }

    #[test]
    fn struct_multi_field_and_generic_variants_are_refused_for_their_reasons() {
        let message = union_error("enum E { #[cultcache(key = 4)] A { id: String } }");
        assert!(message.contains("use a newtype over a #[derive(CultSlots)] struct"), "{message}");
        let message = union_error("enum E { #[cultcache(key = 4)] A(Arm, Arm) }");
        assert!(message.contains("use a newtype over a #[derive(CultSlots)] struct"), "{message}");
        let message = union_error("enum E<T> { #[cultcache(key = 4)] A(Arm), #[cultcache(key = 5)] B(T) }");
        assert!(message.contains("does not support generics"), "{message}");
    }

    // The arm key domain is the reference's Int32: negatives declare, Int32::MAX declares,
    // one past it is a compile error.
    #[test]
    fn the_arm_key_domain_is_int32() {
        union_ok("enum E { #[cultcache(key = -1)] A(Arm), #[cultcache(key = 2147483647)] B, #[cultcache(key = -2147483648)] C }");
        for source in [
            "enum E { #[cultcache(key = 2147483648)] A(Arm) }",
            "enum E { #[cultcache(key = -2147483649)] A(Arm) }",
            "enum E { #[cultcache(key = 4294967296)] A(Arm) }",
        ] {
            let message = union_error(source);
            assert!(message.contains("outside Int32"), "{source}: {message}");
        }
        let message = union_error("enum E { #[cultcache(key = \"1\")] A(Arm) }");
        assert!(message.contains("integer literal"), "{message}");
    }
    fn entry_result(source: &str) -> syn::Result<proc_macro2::TokenStream> {
        database_entry_codec(syn::parse_str(source).expect("the fixture parses"))
    }

    fn slots_result(source: &str) -> syn::Result<proc_macro2::TokenStream> {
        cult_slots_codec(syn::parse_str(source).expect("the fixture parses"))
    }

    // Each attribute names a thing once; the second would silently win otherwise.
    #[test]
    fn a_repeated_type_or_schema_attribute_is_refused_for_that_reason() {
        for source in [
            "#[cultcache(type = \"a.one\")] #[cultcache(type = \"a.two\")] struct S { #[cultcache(key = 0)] id: String }",
            "#[cultcache(type = \"a.one\", type = \"a.two\")] struct S { #[cultcache(key = 0)] id: String }",
            "#[cultcache(type = \"a.one\", schema = \"s.one\")] #[cultcache(schema = \"s.two\")] struct S { #[cultcache(key = 0)] id: String }",
            "#[cultcache(type = \"a.one\", schema = \"s.one\", schema = \"s.two\")] struct S { #[cultcache(key = 0)] id: String }",
        ] {
            let message = entry_result(source).expect_err(source).to_string();
            assert!(message.contains("repeated `type`") || message.contains("repeated `schema`"), "{source}: {message}");
        }
        entry_result("#[cultcache(type = \"a.one\", schema = \"s.one\")] struct S { #[cultcache(key = 0)] id: String }")
            .expect("one type and one schema are accepted");
    }

    #[test]
    fn a_generic_slots_struct_and_a_missing_type_are_refused_for_their_reasons() {
        let message = slots_result("struct S<T> { #[cultcache(key = 0)] id: T }").unwrap_err().to_string();
        assert!(message.contains("does not support generics"), "{message}");
        let message = entry_result("struct S { #[cultcache(key = 0)] id: String }").unwrap_err().to_string();
        assert!(message.contains("requires #[cultcache(type"), "{message}");
    }

    // A slot record is a dense array: keys stop at the largest array16 length.
    #[test]
    fn the_slot_key_domain_ends_at_65535() {
        slots_error_free("struct S { #[cultcache(key = 0)] a: String, #[cultcache(key = 65535)] b: String }");
        for source in [
            "struct S { #[cultcache(key = 65536)] a: String }",
            "struct S { #[cultcache(key = 4294967296)] a: String }",
            "struct S { #[cultcache(key = 0x10000)] a: String }",
        ] {
            let message = slots_error(source);
            assert!(message.contains("outside 0..=65535"), "{source}: {message}");
        }
    }

    fn slots_error_free(source: &str) {
        let input: DeriveInput = syn::parse_str(source).expect("the fixture parses");
        named_slots(input.data, &input.ident, "CultSlots").expect("the fixture is accepted");
    }
}
