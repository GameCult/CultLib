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
                entry_type = Some(meta.value()?.parse()?);
                Ok(())
            } else if meta.path.is_ident("schema") {
                schema_name = Some(meta.value()?.parse()?);
                Ok(())
            } else {
                Err(meta.error("unsupported cultcache attribute"))
            }
        });
        if let Err(error) = parse_result {
            return error.to_compile_error().into();
        }
    }

    let Some(entry_type) = entry_type else {
        return syn::Error::new_spanned(
            ident,
            "DatabaseEntry derive requires #[cultcache(type = \"...\")]",
        )
        .to_compile_error()
        .into();
    };
    let schema_name = schema_name.unwrap_or_else(|| LitStr::new(&ident.to_string(), ident.span()));

    let slots = match named_slots(input.data, &ident, "DatabaseEntry") {
        Ok(slots) => slots,
        Err(error) => return error.to_compile_error().into(),
    };
    let codec = slot_codec(&ident, &slots);

    quote! {
        impl ::cultcache_rs::DatabaseEntry for #ident {
            const TYPE: &'static str = #entry_type;
            const SCHEMA_NAME: &'static str = #schema_name;
        }

        #codec
    }
    .into()
}

/// A named struct whose fields carry `#[cultcache(key = N)]`: the same positional
/// array a `DatabaseEntry` is, without the entry consts. The payload of a union arm,
/// or any nested record that crosses runtimes.
#[proc_macro_derive(CultSlots, attributes(cultcache))]
pub fn derive_cult_slots(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    let ident = input.ident.clone();
    if !input.generics.params.is_empty() {
        return syn::Error::new_spanned(ident, "CultSlots derive does not support generics")
            .to_compile_error()
            .into();
    }
    match named_slots(input.data, &ident, "CultSlots") {
        Ok(slots) => slot_codec(&ident, &slots).into(),
        Err(error) => error.to_compile_error().into(),
    }
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
                    let value: LitInt = meta.value()?.parse()?;
                    key = Some(value.base10_parse()?);
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
        impl ::serde::Serialize for #ident {
            fn serialize<S>(&self, serializer: S) -> ::std::result::Result<S::Ok, S::Error>
            where
                S: ::serde::Serializer,
            {
                use ::serde::ser::SerializeTuple;
                let mut tuple = serializer.serialize_tuple(#max_slot + 1)?;
                #(#serialize_elements)*
                tuple.end()
            }
        }

        impl<'de> ::serde::Deserialize<'de> for #ident {
            fn deserialize<D>(deserializer: D) -> ::std::result::Result<Self, D::Error>
            where
                D: ::serde::Deserializer<'de>,
            {
                struct CultCacheVisitor;

                impl<'de> ::serde::de::Visitor<'de> for CultCacheVisitor {
                    type Value = #ident;

                    fn expecting(&self, formatter: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
                        formatter.write_str(concat!("a CultCache array for ", stringify!(#ident)))
                    }

                    fn visit_seq<A>(self, mut seq: A) -> ::std::result::Result<Self::Value, A::Error>
                    where
                        A: ::serde::de::SeqAccess<'de>,
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

                deserializer.deserialize_tuple(#max_slot + 1, CultCacheVisitor)
            }
        }
    }
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
        key: u64,
        ident: syn::Ident,
        payload: Option<Type>,
    }
    let mut arms: Vec<Arm> = Vec::new();
    for variant in data.variants {
        let mut key: Option<u64> = None;
        for attribute in variant
            .attrs
            .iter()
            .filter(|attr| attr.path().is_ident("cultcache"))
        {
            attribute.parse_nested_meta(|meta| {
                if meta.path.is_ident("key") {
                    let value: LitInt = meta.value()?.parse()?;
                    key = Some(value.base10_parse()?);
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
    let declared = arms
        .iter()
        .map(|arm| arm.key.to_string())
        .collect::<Vec<_>>()
        .join(", ");
    let unknown_message = format!("unknown arm key {{}} for union {enum_name}; declared keys: {declared}");
    let arity_message = format!("union {enum_name} is not a [key, slots] pair");

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
                    let arm = seq
                        .next_element::<#ty>()?
                        .ok_or_else(|| ::serde::de::Error::custom(#arity_message))?;
                    #ident::#variant(arm)
                }
            }
        } else {
            quote! {
                #key => {
                    seq.next_element::<[(); 0]>()
                        .map_err(|_| ::serde::de::Error::custom(#arity_message))?
                        .ok_or_else(|| ::serde::de::Error::custom(#arity_message))?;
                    #ident::#variant
                }
            }
        }
    });

    Ok(quote! {
        impl ::serde::Serialize for #ident {
            fn serialize<S>(&self, serializer: S) -> ::std::result::Result<S::Ok, S::Error>
            where
                S: ::serde::Serializer,
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
            fn deserialize<D>(deserializer: D) -> ::std::result::Result<Self, D::Error>
            where
                D: ::serde::Deserializer<'de>,
            {
                struct CultUnionVisitor;

                impl<'de> ::serde::de::Visitor<'de> for CultUnionVisitor {
                    type Value = #ident;

                    fn expecting(&self, formatter: &mut ::std::fmt::Formatter) -> ::std::fmt::Result {
                        formatter.write_str(concat!("a [key, slots] pair for union ", stringify!(#ident)))
                    }

                    fn visit_seq<A>(self, mut seq: A) -> ::std::result::Result<Self::Value, A::Error>
                    where
                        A: ::serde::de::SeqAccess<'de>,
                    {
                        let key = seq
                            .next_element::<u64>()
                            .map_err(|_| ::serde::de::Error::custom(#arity_message))?
                            .ok_or_else(|| ::serde::de::Error::custom(#arity_message))?;
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

                deserializer.deserialize_tuple(2, CultUnionVisitor)
            }
        }
    })
}
